using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using DoskerTools.EditorUi;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;
using static DoskerTools.EditorUi.DoskerLocale;
using Object = UnityEngine.Object;

// Parte Animator da janela: preview no Edit mode do estado, blend tree ou clip selecionado no Animator,
// aplicado ao avatar que usa o controller: por um Full Controller do VRCFury (com as mesmas regras de caminho do build) ou pelo Avatar
// Descriptor. Tudo passa pelo AnimationMode do Unity, como o preview da janela Animation: os valores
// originais ficam guardados, nada é serializado e tudo volta ao parar.
public sealed partial class VrcEditModePreviewWindow
{
    [Serializable]
    private sealed class AnimatorPreview
    {
        internal const string PrefsFile = "VrcAnimatorPreview";
        internal const double TickInterval = 1.0 / 60.0;
        internal const double RepaintInterval = 1.0 / 15.0;
        internal const double PollInterval = 0.25;
        internal const float MaxStep = 0.1f;
        internal const int MaxWeightRows = 12;
        internal const int MaxMissingRows = 30;
        internal const string VrcfuryTypeName = "VF.Model.VRCFury";
        internal const string FullControllerTypeName = "VF.Model.Feature.FullController";
        internal const string DescriptorTypeName = "VRC.SDK3.Avatars.Components.VRCAvatarDescriptor";
        internal const string EditorOnlyTag = "EditorOnly";
        internal const string DocsUrl = "https://github.com/sdosker/dosker-tools/tree/main/Packages/com.dosker.vrc-edit-mode-preview#readme";

        // VRCAvatarDescriptor.AnimLayerType, na ordem do enum.
        internal static readonly string[] LayerTypeNames = { "Base", "Deprecated", "Additive", "Gesture", "Action", "FX", "Sitting", "TPose", "IKPose" };
        internal static readonly Regex FingerMuscle = new Regex(@"^(Left|Right) (Thumb|Index|Middle|Ring|Little) (.+)$");
        internal static Dictionary<string, int> muscleByCurveName;
        internal static readonly HashSet<AnimatorPreview> Instances = new HashSet<AnimatorPreview>();

        // Onde o controller entra no avatar.
        internal sealed class Target
        {
            public Transform Avatar;
            public Animator Animator;
            // De onde os caminhos das animações partem: o objeto do Full Controller ou o rootObjOverride.
            public Transform Owner;
            public Component Source;
            public bool FromVrcfury;
            public bool RootBindingsApplyToAvatar;
            public readonly List<(string From, string To, bool Delete)> Rewrites = new List<(string, string, bool)>();
            public int LayerType = -1;
            public string Label;

            public bool SameAs(Target other)
            {
                return other != null && Source == other.Source && LayerType == other.LayerType && Owner == other.Owner;
            }

            public bool IsAlive
            {
                get { return Avatar != null && Owner != null && Source != null; }
            }
        }

        // Uma propriedade animada já resolvida no avatar, com o valor que ela tinha antes do preview.
        internal sealed class Track
        {
            public GameObject Target;
            public EditorCurveBinding Binding;
            // Escrito direto, sem clip: m_IsActive (um clip não desativa o objeto de onde é amostrado) e o Transform
            // da raiz do avatar (amostrar a raiz passaria pelo Animator humanoide).
            public bool Direct;
            public string AvatarPath;
            public bool IsObject;
            public bool Valid = true;
            public float Scale = 1f;
            public float DefaultFloat;
            public Object DefaultObject;
            public float Value;
            public Object ObjectValue;
            public float Covered;
            public float BestWeight;
        }

        internal sealed class ClipData
        {
            public AnimationClip Clip;
            public readonly List<(int Track, AnimationCurve Curve)> Floats = new List<(int, AnimationCurve)>();
            public readonly List<(int Track, ObjectReferenceKeyframe[] Keys)> Objects = new List<(int, ObjectReferenceKeyframe[])>();
            public readonly List<(int Muscle, AnimationCurve Curve)> Muscles = new List<(int, AnimationCurve)>();
        }

        // As propriedades de um objeto, amostradas a partir dele mesmo. Amostrar pela raiz passaria pelo Animator
        // humanoide, que joga o corpo inteiro na pose de músculos zerados.
        internal sealed class Group
        {
            public GameObject Target;
            public AnimationClip Clip;
            public readonly List<int> Floats = new List<int>();
            public readonly List<int> Objects = new List<int>();
        }

        internal sealed class Session
        {
            public Target Target;
            public AnimatorController Controller;
            public Motion Motion;
            public AnimatorState State;
            public int LayerIndex = -1;
            public readonly List<Track> Tracks = new List<Track>();
            public readonly Dictionary<(GameObject, Type, string), int> TrackIndex = new Dictionary<(GameObject, Type, string), int>();
            public readonly Dictionary<AnimationClip, ClipData> Clips = new Dictionary<AnimationClip, ClipData>();
            public readonly List<Group> Groups = new List<Group>();
            public readonly SortedSet<string> Missing = new SortedSet<string>(StringComparer.Ordinal);
            public readonly HashSet<int> Muscles = new HashSet<int>();
            public bool[] MaskedMuscles;
            public int IgnoredMuscles;
            public WeightEvaluator Evaluator;
            public int Signature;
            public bool Prepared;
            public HumanPoseHandler Handler;
            public HumanPose RestPose;
            public Vector3 HipsPosition;
            public Quaternion HipsRotation;
            public Transform Hips;
            // Valores originais do que é escrito sem clip. São registrados de novo a cada amostra, porque o
            // EndSampling esquece o que não foi registrado dentro dele, e aí o valor não voltaria ao parar.
            public readonly List<(EditorCurveBinding Binding, PropertyModification Original)> Records = new List<(EditorCurveBinding, PropertyModification)>();
            // As propriedades de Transform animadas, para a simulação de PhysBones partir da pose animada.
            public readonly Dictionary<Transform, List<Track>> TransformTracks = new Dictionary<Transform, List<Track>>();

            public bool NeedsHumanoid
            {
                get { return Handler != null; }
            }

            public void Dispose()
            {
                Evaluator?.Dispose();
                Evaluator = null;
                Handler?.Dispose();
                Handler = null;
                foreach (var group in Groups)
                {
                    if (group.Clip != null)
                    {
                        DestroyImmediate(group.Clip);
                    }
                }
                Groups.Clear();
            }
        }

        // Avalia a blend tree num Animator escondido numa preview scene, só para ler os pesos de cada clip.
        // Tocar o controller no avatar aplicaria a pose humanoide padrão; os pesos vêm do próprio Unity.
        internal sealed class WeightEvaluator : IDisposable
        {
            internal Scene scene;
            internal GameObject host;
            internal AnimatorController controller;
            internal AnimatorStateMachine machine;
            internal AnimatorState state;
            internal PlayableGraph graph;
            internal AnimatorControllerPlayable playable;
            internal readonly List<AnimatorClipInfo> infos = new List<AnimatorClipInfo>();
            internal readonly string[] parameters;

            public WeightEvaluator(Motion motion, IEnumerable<string> floatParameters)
            {
                parameters = floatParameters.ToArray();
                scene = EditorSceneManager.NewPreviewScene();
                host = EditorUtility.CreateGameObjectWithHideFlags("Dosker Animator Preview", HideFlags.HideAndDontSave);
                SceneManager.MoveGameObjectToScene(host, scene);
                var animator = host.AddComponent<Animator>();

                // Montado pelos setters: AddLayer e AddState registram Undo e sujariam a cena.
                controller = new AnimatorController { name = "Dosker Animator Preview", hideFlags = HideFlags.HideAndDontSave };
                state = new AnimatorState { name = "Preview", motion = motion, hideFlags = HideFlags.HideAndDontSave };
                machine = new AnimatorStateMachine { name = "Preview", hideFlags = HideFlags.HideAndDontSave };
                machine.states = new[] { new ChildAnimatorState { state = state, position = Vector3.zero } };
                machine.defaultState = state;
                controller.parameters = parameters
                    .Select(name => new AnimatorControllerParameter { name = name, type = AnimatorControllerParameterType.Float })
                    .ToArray();
                controller.layers = new[] { new AnimatorControllerLayer { name = "Preview", stateMachine = machine, defaultWeight = 1f } };

                graph = PlayableGraph.Create("Dosker Animator Preview");
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                playable = AnimatorControllerPlayable.Create(graph, controller);
                AnimationPlayableOutput.Create(graph, "Preview", animator).SetSourcePlayable(playable);
            }

            public float Evaluate(float normalizedTime, IReadOnlyDictionary<string, float> values, List<(AnimationClip Clip, float Weight)> output)
            {
                foreach (var name in parameters)
                {
                    playable.SetFloat(name, values.TryGetValue(name, out var value) ? value : 0f);
                }
                playable.Play(state.nameHash, 0, normalizedTime);
                graph.Evaluate(0f);
                infos.Clear();
                playable.GetCurrentAnimatorClipInfo(0, infos);
                output.Clear();
                foreach (var info in infos)
                {
                    if (info.clip != null)
                    {
                        output.Add((info.clip, info.weight));
                    }
                }
                return playable.GetCurrentAnimatorStateInfo(0).length;
            }

            public void Dispose()
            {
                if (graph.IsValid())
                {
                    graph.Destroy();
                }
                if (host != null)
                {
                    DestroyImmediate(host);
                }
                if (scene.IsValid())
                {
                    EditorSceneManager.ClosePreviewScene(scene);
                }
                foreach (var temporary in new Object[] { state, machine, controller })
                {
                    if (temporary != null)
                    {
                        DestroyImmediate(temporary);
                    }
                }
            }
        }

        [Serializable]
        internal sealed class Preferences
        {
            public float speed = 1f;
            public bool loop = true;
            public bool followAnimator = true;
        }

        internal static Type vrcfuryType;
        internal static Type descriptorType;
        internal static Type animatorToolType;
        internal static MethodInfo getInputBlendValue;
        internal static bool sdkHooked;

        // Preview ligado é a única coisa que sobrevive a um reload de scripts: a pose volta a ser lida do zero.
        [SerializeField] internal bool previewOn;
        [SerializeField] internal bool playing = true;
        [SerializeField] internal float normalizedTime;
        [SerializeField] internal bool lockSelection;

        internal float speed = 1f;
        internal bool loop = true;
        internal bool followAnimator = true;

        internal AnimationModeDriver driver;
        internal Session session;
        internal AnimatorController selectedController;
        internal Motion selectedMotion;
        internal AnimatorState selectedState;
        internal readonly List<Target> targets = new List<Target>();
        internal Target chosenTarget;
        internal readonly Dictionary<string, float> parameterValues = new Dictionary<string, float>();
        internal readonly Dictionary<string, float> animatorValues = new Dictionary<string, float>();
        internal readonly List<(AnimationClip Clip, float Weight)> weights = new List<(AnimationClip, float)>();
        // Criado no primeiro uso: a API do Unity não pode ser chamada no construtor da janela.
        internal float[] muscleSums;
        internal float stateLength;
        internal bool selectionDirty = true;
        internal bool targetsDirty = true;
        internal bool sampleRequested;
        internal bool blockedByOtherTool;
        internal double lastTick;
        internal double lastRepaint;
        internal double lastPoll;
        internal Vector2 scroll;
        internal bool showMissing;
        internal string lastError;

        // A janela que contém o preview: a simulação de PhysBones, o Repaint e o ciclo de vida vêm dela.
        [NonSerialized] internal VrcEditModePreviewWindow Owner;

        internal void Enable(VrcEditModePreviewWindow owner)
        {
            Owner = owner;
            Instances.Add(this);
            var saved = DoskerSettings<Preferences>.Load(PrefsFile);
            speed = saved.speed;
            loop = saved.loop;
            followAnimator = saved.followAnimator;
            ResolveTypes();
            HookSdkBuilds();

            Selection.selectionChanged += MarkSelectionDirty;
            EditorApplication.hierarchyChanged += MarkTargetsDirty;
            EditorApplication.playModeStateChanged += HandlePlayModeChanged;
            AssemblyReloadEvents.beforeAssemblyReload += StopPreviewMode;
            EditorApplication.quitting += StopPreviewMode;
            EditorSceneManager.sceneSaving += HandleSceneSaving;
            EditorSceneManager.sceneClosing += HandleSceneClosing;
            EditorSceneManager.sceneOpened += HandleSceneOpened;
            Undo.undoRedoPerformed += RequestSample;
            lastTick = EditorApplication.timeSinceStartup;
        }

        internal void Disable()
        {
            DoskerSettings<Preferences>.Save(PrefsFile, new Preferences { speed = speed, loop = loop, followAnimator = followAnimator });
            EndSession();
            StopPreviewMode();
            if (driver != null)
            {
                DestroyImmediate(driver);
                driver = null;
            }
            Instances.Remove(this);

            Selection.selectionChanged -= MarkSelectionDirty;
            EditorApplication.hierarchyChanged -= MarkTargetsDirty;
            EditorApplication.playModeStateChanged -= HandlePlayModeChanged;
            AssemblyReloadEvents.beforeAssemblyReload -= StopPreviewMode;
            EditorApplication.quitting -= StopPreviewMode;
            EditorSceneManager.sceneSaving -= HandleSceneSaving;
            EditorSceneManager.sceneClosing -= HandleSceneClosing;
            EditorSceneManager.sceneOpened -= HandleSceneOpened;
            Undo.undoRedoPerformed -= RequestSample;
            SceneView.RepaintAll();
        }

        internal void MarkSelectionDirty()
        {
            selectionDirty = true;
        }

        internal void MarkTargetsDirty()
        {
            targetsDirty = true;
        }

        internal void RequestSample()
        {
            sampleRequested = true;
        }

        internal void HandlePlayModeChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingEditMode)
            {
                EndSession();
                StopPreviewMode();
            }
            targetsDirty = true;
        }

        // Os valores do AnimationMode nunca chegam ao arquivo, mas a cena é salva com a pose original por garantia.
        // O próximo tick aplica o preview de novo.
        internal void HandleSceneSaving(Scene scene, string path)
        {
            StopPreviewMode();
        }

        internal void HandleSceneClosing(Scene scene, bool removingScene)
        {
            EndSession();
            StopPreviewMode();
            targetsDirty = true;
        }

        internal void HandleSceneOpened(Scene scene, OpenSceneMode mode)
        {
            targetsDirty = true;
        }

        // ---------------------------------------------------------------- SDK e VRCFury por reflexão

        internal static void ResolveTypes()
        {
            vrcfuryType = FindType(VrcfuryTypeName);
            descriptorType = FindType(DescriptorTypeName);
            animatorToolType = FindType("UnityEditor.Graphs.AnimatorControllerTool");
            getInputBlendValue = typeof(BlendTree).GetMethod("GetInputBlendValue", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, new[] { typeof(string) }, null);
        }

        internal static Type FindType(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType(fullName, false);
                if (type != null)
                {
                    return type;
                }
            }
            return null;
        }

        // O SDK clona o avatar para o build: com o preview ligado, o clone levaria os valores animados para o
        // upload. OnSdkBuildStart dispara antes do clone, então o preview para ali.
        internal static void HookSdkBuilds()
        {
            if (sdkHooked)
            {
                return;
            }
            var panelType = FindType("VRCSdkControlPanel");
            var panelEvent = panelType?.GetEvent("OnSdkPanelEnable", BindingFlags.Public | BindingFlags.Static);
            if (panelEvent == null)
            {
                return;
            }
            try
            {
                panelEvent.AddEventHandler(null, new EventHandler(HandleSdkPanelEnable));
                sdkHooked = true;
                HandleSdkPanelEnable(null, EventArgs.Empty);
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[Dosker Animator Preview] SDK build hook unavailable: " + exception.Message);
            }
        }

        internal static void HandleSdkPanelEnable(object sender, EventArgs args)
        {
            var panelType = FindType("VRCSdkControlPanel");
            var builderType = FindType("VRC.SDK3A.Editor.IVRCSdkAvatarBuilderApi");
            var baseType = FindType("VRC.SDKBase.Editor.IVRCSdkBuilderApi");
            var tryGet = panelType?.GetMethod("TryGetBuilder", BindingFlags.Public | BindingFlags.Static);
            var buildEvent = baseType?.GetEvent("OnSdkBuildStart");
            if (tryGet == null || builderType == null || buildEvent == null)
            {
                return;
            }
            try
            {
                var arguments = new object[] { null };
                if (!(bool)tryGet.MakeGenericMethod(builderType).Invoke(null, arguments) || arguments[0] == null)
                {
                    return;
                }
                var handler = new EventHandler<object>(HandleSdkBuildStart);
                buildEvent.RemoveEventHandler(arguments[0], handler);
                buildEvent.AddEventHandler(arguments[0], handler);
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[Dosker Animator Preview] SDK build hook unavailable: " + exception.Message);
            }
        }

        internal static void HandleSdkBuildStart(object sender, object target)
        {
            foreach (var window in Instances.ToList())
            {
                if (window != null && window.previewOn)
                {
                    window.previewOn = false;
                    window.EndSession();
                    window.StopPreviewMode();
                    Debug.Log("[Dosker Animator Preview] " + Tr("Preview desligado para o build do VRChat SDK.", "Preview turned off for the VRChat SDK build."));
                }
            }
        }

        internal static AnimatorController AnimatorWindowController()
        {
            if (animatorToolType == null)
            {
                return null;
            }
            const BindingFlags Flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            var tool = animatorToolType.GetField("tool", Flags)?.GetValue(null) ?? animatorToolType.GetProperty("tool", Flags)?.GetValue(null);
            if (!(tool is Object toolObject) || toolObject == null)
            {
                return null;
            }
            return animatorToolType.GetProperty("animatorController", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(tool) as AnimatorController;
        }

        internal static bool IsEditorOnly(Transform transform)
        {
            for (var current = transform; current != null; current = current.parent)
            {
                if (current.CompareTag(EditorOnlyTag))
                {
                    return true;
                }
            }
            return false;
        }

        internal static bool InEditableScene(Component component)
        {
            var scene = component.gameObject.scene;
            return scene.IsValid() && scene.isLoaded && !EditorSceneManager.IsPreviewScene(scene) && !EditorUtility.IsPersistent(component);
        }

        internal static string LayerTypeName(int type)
        {
            return type >= 0 && type < LayerTypeNames.Length ? LayerTypeNames[type] : type.ToString(CultureInfo.InvariantCulture);
        }

        internal static bool References(SerializedProperty wrapper, AnimatorController controller)
        {
            var reference = wrapper.FindPropertyRelative("objRef")?.objectReferenceValue;
            if (reference != null)
            {
                return reference == controller;
            }
            // Sem objRef, o VRCFury guarda "guid:fileID|caminho" ou "guid|caminho".
            var id = wrapper.FindPropertyRelative("id")?.stringValue;
            if (string.IsNullOrEmpty(id))
            {
                return false;
            }
            var guid = id.Split(':', '|')[0];
            return guid == AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(controller));
        }

        // Todos os lugares da cena que usam o controller: Full Controllers do VRCFury e camadas do Avatar Descriptor.
        internal static List<Target> FindTargets(AnimatorController controller)
        {
            var found = new List<Target>();
            if (controller == null || descriptorType == null)
            {
                return found;
            }

            if (vrcfuryType != null)
            {
                foreach (var component in Object.FindObjectsOfType(vrcfuryType, true).OfType<Component>())
                {
                    if (!InEditableScene(component) || IsEditorOnly(component.transform))
                    {
                        continue;
                    }
                    var serialized = new SerializedObject(component);
                    var content = serialized.FindProperty("content");
                    if (content == null || !content.managedReferenceFullTypename.EndsWith(FullControllerTypeName, StringComparison.Ordinal))
                    {
                        continue;
                    }
                    var avatar = component.GetComponentInParent(descriptorType, true);
                    var entries = serialized.FindProperty("content.controllers");
                    if (avatar == null || entries == null)
                    {
                        continue;
                    }
                    for (var i = 0; i < entries.arraySize; i++)
                    {
                        var entry = entries.GetArrayElementAtIndex(i);
                        var wrapper = entry.FindPropertyRelative("controller");
                        if (wrapper == null || !References(wrapper, controller))
                        {
                            continue;
                        }
                        var rootOverride = serialized.FindProperty("content.rootObjOverride")?.objectReferenceValue as GameObject;
                        var target = new Target
                        {
                            Avatar = avatar.transform,
                            Animator = avatar.GetComponent<Animator>(),
                            Owner = rootOverride != null ? rootOverride.transform : component.transform,
                            Source = component,
                            FromVrcfury = true,
                            RootBindingsApplyToAvatar = serialized.FindProperty("content.rootBindingsApplyToAvatar")?.boolValue ?? false,
                            LayerType = entry.FindPropertyRelative("type")?.intValue ?? 5,
                        };
                        var rewrites = serialized.FindProperty("content.rewriteBindings");
                        for (var r = 0; rewrites != null && r < rewrites.arraySize; r++)
                        {
                            var rewrite = rewrites.GetArrayElementAtIndex(r);
                            target.Rewrites.Add((
                                rewrite.FindPropertyRelative("from")?.stringValue ?? "",
                                rewrite.FindPropertyRelative("to")?.stringValue ?? "",
                                rewrite.FindPropertyRelative("delete")?.boolValue ?? false));
                        }
                        var ownerPath = target.Owner == target.Avatar ? "" : " › " + AnimationUtility.CalculateTransformPath(target.Owner, target.Avatar);
                        target.Label = avatar.name + ownerPath + " · Full Controller (" + LayerTypeName(target.LayerType) + ")";
                        found.Add(target);
                    }
                }
            }

            foreach (var avatar in Object.FindObjectsOfType(descriptorType, true).OfType<Component>())
            {
                if (!InEditableScene(avatar) || IsEditorOnly(avatar.transform))
                {
                    continue;
                }
                var serialized = new SerializedObject(avatar);
                foreach (var listName in new[] { "baseAnimationLayers", "specialAnimationLayers" })
                {
                    var layers = serialized.FindProperty(listName);
                    for (var i = 0; layers != null && i < layers.arraySize; i++)
                    {
                        var layer = layers.GetArrayElementAtIndex(i);
                        if (layer.FindPropertyRelative("isDefault")?.boolValue == true || layer.FindPropertyRelative("animatorController")?.objectReferenceValue != controller)
                        {
                            continue;
                        }
                        var type = layer.FindPropertyRelative("type")?.intValue ?? -1;
                        found.Add(new Target
                        {
                            Avatar = avatar.transform,
                            Animator = avatar.GetComponent<Animator>(),
                            Owner = avatar.transform,
                            Source = avatar,
                            RootBindingsApplyToAvatar = true,
                            LayerType = type,
                            Label = avatar.name + " · Avatar Descriptor (" + LayerTypeName(type) + ")",
                        });
                    }
                }
            }
            return found;
        }

        // Os controllers da cena que tocam este clip, para quando a seleção é um .anim no Project.
        internal static AnimatorController FindControllerForClip(AnimationClip clip)
        {
            if (vrcfuryType == null && descriptorType == null)
            {
                return null;
            }
            var controllers = new List<AnimatorController>();
            if (vrcfuryType != null)
            {
                foreach (var component in Object.FindObjectsOfType(vrcfuryType, true).OfType<Component>().Where(InEditableScene))
                {
                    var entries = new SerializedObject(component).FindProperty("content.controllers");
                    for (var i = 0; entries != null && i < entries.arraySize; i++)
                    {
                        if (entries.GetArrayElementAtIndex(i).FindPropertyRelative("controller.objRef")?.objectReferenceValue is AnimatorController controller)
                        {
                            controllers.Add(controller);
                        }
                    }
                }
            }
            if (descriptorType != null)
            {
                foreach (var avatar in Object.FindObjectsOfType(descriptorType, true).OfType<Component>().Where(InEditableScene))
                {
                    var serialized = new SerializedObject(avatar);
                    foreach (var listName in new[] { "baseAnimationLayers", "specialAnimationLayers" })
                    {
                        var layers = serialized.FindProperty(listName);
                        for (var i = 0; layers != null && i < layers.arraySize; i++)
                        {
                            if (layers.GetArrayElementAtIndex(i).FindPropertyRelative("animatorController")?.objectReferenceValue is AnimatorController controller)
                            {
                                controllers.Add(controller);
                            }
                        }
                    }
                }
            }
            return controllers.FirstOrDefault(controller => controller.animationClips.Contains(clip));
        }

        // ---------------------------------------------------------------- Caminhos, como o VRCFury resolve

        // AnimationBindingUtils.RewriteRelativePath do VRCFury.
        internal static string RewritePath(string path, List<(string From, string To, bool Delete)> rewrites)
        {
            string Append(string to, string suffix)
            {
                if (suffix == null)
                {
                    return to;
                }
                if (to == "" || suffix.StartsWith("/", StringComparison.Ordinal))
                {
                    return suffix;
                }
                if (suffix == "")
                {
                    return to;
                }
                return to == "/" ? "/" + suffix : to + "/" + suffix;
            }

            foreach (var rewrite in rewrites)
            {
                var from = rewrite.From ?? "";
                while (from.Length > 1 && from.EndsWith("/", StringComparison.Ordinal))
                {
                    from = from.Substring(0, from.Length - 1);
                }
                var to = rewrite.To ?? "";
                while (to.Length > 1 && to.EndsWith("/", StringComparison.Ordinal))
                {
                    to = to.Substring(0, to.Length - 1);
                }

                if (from == "")
                {
                    path = Append(to, path);
                }
                else if (path.StartsWith(from + "/", StringComparison.Ordinal))
                {
                    path = Append(to, path.Substring(from.Length + 1));
                }
                else if (path == from)
                {
                    path = to;
                }
                else
                {
                    continue;
                }
                if (rewrite.Delete)
                {
                    return null;
                }
            }
            return path;
        }

        internal static Transform FindPath(Transform from, Transform root, string path)
        {
            if (path == "")
            {
                return from;
            }
            var current = path.StartsWith("/", StringComparison.Ordinal) ? root : from;
            foreach (var part in path.Split('/'))
            {
                if (part == "" || part == ".")
                {
                    continue;
                }
                if (part == "..")
                {
                    if (current == root)
                    {
                        return null;
                    }
                    current = current.parent;
                }
                else
                {
                    current = current.Find(part);
                }
                if (current == null)
                {
                    return null;
                }
            }
            return current;
        }

        internal static bool IsValidTarget(Transform target, Type type, Transform root)
        {
            if (target == null || !(target == root || target.IsChildOf(root)) || type == null)
            {
                return false;
            }
            if (type == typeof(GameObject) || type == typeof(Animator))
            {
                return true;
            }
            return typeof(Component).IsAssignableFrom(type) && target.GetComponent(type) != null;
        }

        // AnimationBindingUtils.ResolveTarget do VRCFury: tenta a partir do dono e sobe até a raiz do avatar.
        internal static Transform ResolveTarget(Target target, string path, Type type)
        {
            path = RewritePath(path, target.Rewrites);
            if (path == null)
            {
                return null;
            }
            if (path == "" && target.RootBindingsApplyToAvatar)
            {
                return target.Avatar;
            }
            if (path.StartsWith("/", StringComparison.Ordinal))
            {
                var absolute = FindPath(target.Avatar, target.Avatar, path.TrimStart('/'));
                return IsValidTarget(absolute, type, target.Avatar) ? absolute : null;
            }
            if (!(target.Owner == target.Avatar || target.Owner.IsChildOf(target.Avatar)))
            {
                return null;
            }
            for (var current = target.Owner; current != null; current = current.parent)
            {
                var found = FindPath(current, target.Avatar, path);
                if (IsValidTarget(found, type, target.Avatar))
                {
                    return found;
                }
                if (current == target.Avatar)
                {
                    break;
                }
            }
            return null;
        }

        // ---------------------------------------------------------------- Músculos

        internal static Dictionary<string, int> MuscleByCurveName
        {
            get
            {
                if (muscleByCurveName == null)
                {
                    muscleByCurveName = new Dictionary<string, int>(StringComparer.Ordinal);
                    for (var i = 0; i < HumanTrait.MuscleCount; i++)
                    {
                        var name = HumanTrait.MuscleName[i];
                        muscleByCurveName[name] = i;
                        // Nos clips, os dedos usam outro nome: "LeftHand.Index.1 Stretched".
                        var finger = FingerMuscle.Match(name);
                        if (finger.Success)
                        {
                            muscleByCurveName[finger.Groups[1].Value + "Hand." + finger.Groups[2].Value + "." + finger.Groups[3].Value] = i;
                        }
                    }
                }
                return muscleByCurveName;
            }
        }

        internal static AvatarMaskBodyPart BodyPartOf(int muscle)
        {
            var bone = (HumanBodyBones)HumanTrait.BoneFromMuscle(muscle);
            switch (bone)
            {
                case HumanBodyBones.Hips:
                case HumanBodyBones.Spine:
                case HumanBodyBones.Chest:
                case HumanBodyBones.UpperChest:
                    return AvatarMaskBodyPart.Body;
                case HumanBodyBones.Neck:
                case HumanBodyBones.Head:
                case HumanBodyBones.LeftEye:
                case HumanBodyBones.RightEye:
                case HumanBodyBones.Jaw:
                    return AvatarMaskBodyPart.Head;
                case HumanBodyBones.LeftUpperLeg:
                case HumanBodyBones.LeftLowerLeg:
                case HumanBodyBones.LeftFoot:
                case HumanBodyBones.LeftToes:
                    return AvatarMaskBodyPart.LeftLeg;
                case HumanBodyBones.RightUpperLeg:
                case HumanBodyBones.RightLowerLeg:
                case HumanBodyBones.RightFoot:
                case HumanBodyBones.RightToes:
                    return AvatarMaskBodyPart.RightLeg;
                case HumanBodyBones.LeftShoulder:
                case HumanBodyBones.LeftUpperArm:
                case HumanBodyBones.LeftLowerArm:
                case HumanBodyBones.LeftHand:
                    return AvatarMaskBodyPart.LeftArm;
                case HumanBodyBones.RightShoulder:
                case HumanBodyBones.RightUpperArm:
                case HumanBodyBones.RightLowerArm:
                case HumanBodyBones.RightHand:
                    return AvatarMaskBodyPart.RightArm;
            }
            return bone >= HumanBodyBones.LeftThumbProximal && bone <= HumanBodyBones.LeftLittleDistal
                ? AvatarMaskBodyPart.LeftFingers
                : AvatarMaskBodyPart.RightFingers;
        }

        // ---------------------------------------------------------------- Seleção

        internal static IEnumerable<(AnimatorState State, int Layer)> AllStates(AnimatorController controller)
        {
            for (var layer = 0; layer < controller.layers.Length; layer++)
            {
                var machines = new Stack<AnimatorStateMachine>();
                if (controller.layers[layer].stateMachine != null)
                {
                    machines.Push(controller.layers[layer].stateMachine);
                }
                while (machines.Count > 0)
                {
                    var machine = machines.Pop();
                    foreach (var child in machine.states)
                    {
                        if (child.state != null)
                        {
                            yield return (child.state, layer);
                        }
                    }
                    foreach (var child in machine.stateMachines)
                    {
                        if (child.stateMachine != null)
                        {
                            machines.Push(child.stateMachine);
                        }
                    }
                }
            }
        }

        internal static bool TreeContains(Motion root, Motion motion)
        {
            if (root == motion)
            {
                return true;
            }
            return root is BlendTree tree && tree.children.Any(child => child.motion != null && TreeContains(child.motion, motion));
        }

        internal static void CollectClips(Motion motion, HashSet<AnimationClip> clips, HashSet<BlendTree> visited)
        {
            if (motion is AnimationClip clip)
            {
                clips.Add(clip);
            }
            else if (motion is BlendTree tree && visited.Add(tree))
            {
                foreach (var child in tree.children)
                {
                    CollectClips(child.motion, clips, visited);
                }
            }
        }

        // Os parâmetros float que a blend tree lê, com o intervalo dos thresholds e posições.
        internal static void CollectParameters(Motion motion, Dictionary<string, Vector2> ranges, HashSet<BlendTree> visited)
        {
            if (!(motion is BlendTree tree) || !visited.Add(tree))
            {
                return;
            }
            void Add(string name, float min, float max)
            {
                if (string.IsNullOrEmpty(name))
                {
                    return;
                }
                ranges[name] = ranges.TryGetValue(name, out var range) ? new Vector2(Mathf.Min(range.x, min), Mathf.Max(range.y, max)) : new Vector2(min, max);
            }

            var children = tree.children;
            switch (tree.blendType)
            {
                case BlendTreeType.Simple1D:
                    Add(tree.blendParameter, children.Length > 0 ? children.Min(c => c.threshold) : 0f, children.Length > 0 ? children.Max(c => c.threshold) : 1f);
                    break;
                case BlendTreeType.Direct:
                    foreach (var child in children)
                    {
                        Add(child.directBlendParameter, 0f, 1f);
                    }
                    break;
                default:
                    Add(tree.blendParameter, children.Length > 0 ? children.Min(c => c.position.x) : -1f, children.Length > 0 ? children.Max(c => c.position.x) : 1f);
                    Add(tree.blendParameterY, children.Length > 0 ? children.Min(c => c.position.y) : -1f, children.Length > 0 ? children.Max(c => c.position.y) : 1f);
                    break;
            }
            foreach (var child in children)
            {
                CollectParameters(child.motion, ranges, visited);
            }
        }

        // O que a seleção atual pede: um estado, uma blend tree ou um clip, e o controller de onde vêm.
        internal void ResolveSelection()
        {
            selectionDirty = false;
            if (lockSelection && selectedMotion != null)
            {
                return;
            }
            var active = Selection.activeObject;
            AnimatorController controller = null;
            Motion motion = null;
            AnimatorState state = null;

            switch (active)
            {
                case AnimatorState animatorState:
                    state = animatorState;
                    motion = animatorState.motion;
                    controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(AssetDatabase.GetAssetPath(animatorState));
                    break;
                case BlendTree tree:
                    motion = tree;
                    controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(AssetDatabase.GetAssetPath(tree));
                    break;
                case AnimationClip clip:
                    motion = clip;
                    var windowController = AnimatorWindowController();
                    controller = windowController != null && windowController.animationClips.Contains(clip)
                        ? windowController
                        : selectedController != null && selectedController.animationClips.Contains(clip) ? selectedController : FindControllerForClip(clip);
                    break;
                default:
                    // Transições, máquinas de estado e o próprio controller mantêm o que já estava em preview.
                    return;
            }

            if (controller == null)
            {
                controller = AnimatorWindowController();
            }
            if (state == null && controller != null && motion != null)
            {
                state = AllStates(controller).FirstOrDefault(entry => entry.State.motion != null && TreeContains(entry.State.motion, motion)).State;
            }
            if (controller != selectedController)
            {
                targetsDirty = true;
            }
            selectedController = controller;
            selectedMotion = motion;
            selectedState = state;
        }

        internal void RefreshTargets()
        {
            targetsDirty = false;
            targets.Clear();
            targets.AddRange(FindTargets(selectedController));
            var keep = targets.FirstOrDefault(target => target.SameAs(chosenTarget));
            chosenTarget = keep ?? targets.FirstOrDefault();
        }

        // ---------------------------------------------------------------- Sessão

        internal int ComputeSignature(AnimatorController controller, Motion motion)
        {
            unchecked
            {
                var signature = 17;
                if (motion == null)
                {
                    return signature;
                }
                var clips = new HashSet<AnimationClip>();
                var trees = new HashSet<BlendTree>();
                CollectClips(motion, clips, trees);
                foreach (var item in clips.Cast<Object>().Concat(trees))
                {
                    signature = signature * 31 + item.GetInstanceID();
                    signature = signature * 31 + EditorUtility.GetDirtyCount(item);
                }
                if (controller != null)
                {
                    signature = signature * 31 + EditorUtility.GetDirtyCount(controller);
                }
                return signature;
            }
        }

        internal bool SessionMatchesSelection()
        {
            return session != null && session.Motion == selectedMotion && session.Controller == selectedController && session.Target.SameAs(chosenTarget);
        }

        internal void EndSession()
        {
            if (session == null)
            {
                return;
            }
            session.Dispose();
            session = null;
        }

        internal void BeginSession()
        {
            EndSession();
            StopPreviewMode();
            lastError = null;
            if (selectedMotion == null || chosenTarget == null || !chosenTarget.IsAlive)
            {
                return;
            }

            var built = new Session
            {
                Target = chosenTarget,
                Controller = selectedController,
                Motion = selectedMotion,
                State = selectedState,
            };
            try
            {
                BuildTracks(built);
                built.Signature = ComputeSignature(selectedController, selectedMotion);
                if (selectedMotion is BlendTree)
                {
                    var ranges = new Dictionary<string, Vector2>();
                    CollectParameters(selectedMotion, ranges, new HashSet<BlendTree>());
                    built.Evaluator = new WeightEvaluator(selectedMotion, ranges.Keys);
                }
            }
            catch (Exception exception)
            {
                built.Dispose();
                lastError = exception.Message;
                Debug.LogException(exception);
                return;
            }
            session = built;
        }

        internal void BuildTracks(Session built)
        {
            var target = built.Target;
            var clips = new HashSet<AnimationClip>();
            CollectClips(built.Motion, clips, new HashSet<BlendTree>());

            if (built.Controller != null && built.State != null)
            {
                built.LayerIndex = AllStates(built.Controller).FirstOrDefault(entry => entry.State == built.State).Layer;
            }
            var mask = built.Controller != null && built.LayerIndex >= 0 && built.LayerIndex < built.Controller.layers.Length
                ? built.Controller.layers[built.LayerIndex].avatarMask
                : null;
            var humanoid = target.Animator != null && target.Animator.isHuman && target.Animator.avatar != null;
            built.MaskedMuscles = new bool[HumanTrait.MuscleCount];
            if (mask != null && mask.humanoidBodyPartCount > 0)
            {
                for (var muscle = 0; muscle < HumanTrait.MuscleCount; muscle++)
                {
                    built.MaskedMuscles[muscle] = !mask.GetHumanoidBodyPartActive(BodyPartOf(muscle));
                }
            }

            foreach (var clip in clips)
            {
                var data = new ClipData { Clip = clip };
                built.Clips[clip] = data;
                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                {
                    if (binding.type == typeof(Animator))
                    {
                        // Músculos vão pelo HumanPoseHandler; parâmetros do animator, root motion e IK ficam de fora.
                        if (MuscleByCurveName.TryGetValue(binding.propertyName, out var muscle))
                        {
                            if (!humanoid || built.MaskedMuscles[muscle])
                            {
                                built.IgnoredMuscles++;
                                continue;
                            }
                            data.Muscles.Add((muscle, AnimationUtility.GetEditorCurve(clip, binding)));
                            built.Muscles.Add(muscle);
                        }
                        continue;
                    }
                    var track = TrackFor(built, binding, false);
                    if (track >= 0)
                    {
                        data.Floats.Add((track, AnimationUtility.GetEditorCurve(clip, binding)));
                    }
                }
                foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                {
                    var track = TrackFor(built, binding, true);
                    if (track >= 0)
                    {
                        data.Objects.Add((track, AnimationUtility.GetObjectReferenceCurve(clip, binding)));
                    }
                }
            }

            foreach (var index in Enumerable.Range(0, built.Tracks.Count))
            {
                var track = built.Tracks[index];
                if (track.Direct)
                {
                    continue;
                }
                var group = built.Groups.FirstOrDefault(item => item.Target == track.Target);
                if (group == null)
                {
                    group = new Group
                    {
                        Target = track.Target,
                        Clip = new AnimationClip { name = "Dosker Animator Preview", hideFlags = HideFlags.HideAndDontSave },
                    };
                    built.Groups.Add(group);
                }
                (track.IsObject ? group.Objects : group.Floats).Add(index);
            }

            foreach (var track in built.Tracks.Where(track => !track.IsObject && track.Binding.type == typeof(Transform)))
            {
                if (!built.TransformTracks.TryGetValue(track.Target.transform, out var list))
                {
                    built.TransformTracks[track.Target.transform] = list = new List<Track>();
                }
                list.Add(track);
            }

            if (humanoid && built.Muscles.Count > 0)
            {
                built.Handler = new HumanPoseHandler(target.Animator.avatar, target.Animator.transform);
                built.Hips = target.Animator.GetBoneTransform(HumanBodyBones.Hips);
            }
        }

        internal static int TrackFor(Session built, EditorCurveBinding binding, bool isObject)
        {
            var target = built.Target;
            var resolved = ResolveTarget(target, binding.path, binding.type);
            if (resolved == null)
            {
                var path = string.IsNullOrEmpty(binding.path) ? "(root)" : binding.path;
                built.Missing.Add(path + " · " + (binding.type != null ? binding.type.Name : "?") + "." + binding.propertyName);
                return -1;
            }
            var key = (resolved.gameObject, binding.type, binding.propertyName);
            if (built.TrackIndex.TryGetValue(key, out var existing))
            {
                return existing;
            }
            var local = binding;
            local.path = "";
            var track = new Track { Target = resolved.gameObject, Binding = local, IsObject = isObject };
            if (!isObject && binding.type == typeof(GameObject) && binding.propertyName == "m_IsActive")
            {
                track.Direct = true;
            }
            else if (resolved == target.Avatar)
            {
                if (isObject || binding.type != typeof(Transform) || !IsDirectTransformProperty(binding.propertyName))
                {
                    built.Missing.Add("(root) · " + binding.type?.Name + "." + binding.propertyName + Tr(" (não suportado na raiz do avatar)", " (not supported on the avatar root)"));
                    return -1;
                }
                track.Direct = true;
            }
            if (track.Direct)
            {
                track.AvatarPath = AnimationUtility.CalculateTransformPath(resolved, target.Avatar);
            }
            // O VRCFury ajusta a escala da raiz para o avatar continuar do mesmo tamanho (AdjustRootScale).
            if (target.FromVrcfury && !isObject && resolved == target.Avatar && binding.type == typeof(Transform) && binding.propertyName.StartsWith("m_LocalScale.", StringComparison.Ordinal))
            {
                var axis = binding.propertyName[binding.propertyName.Length - 1];
                var scale = resolved.localScale;
                track.Scale = axis == 'x' ? scale.x : axis == 'y' ? scale.y : scale.z;
            }
            built.Tracks.Add(track);
            built.TrackIndex[key] = built.Tracks.Count - 1;
            return built.Tracks.Count - 1;
        }

        // Logo depois de o AnimationMode começar, antes de qualquer amostra: lê os valores originais e registra os ossos.
        internal void PrepareSession()
        {
            if (Owner != null && Owner.IsSimulationActive)
            {
                Owner.RestoreAllChains();
            }
            session.Records.Clear();
            foreach (var track in session.Tracks)
            {
                if (track.Target == null)
                {
                    track.Valid = false;
                    continue;
                }
                if (track.IsObject)
                {
                    track.Valid = AnimationUtility.GetObjectReferenceValue(track.Target, track.Binding, out track.DefaultObject);
                }
                else
                {
                    if (track.Binding.type == typeof(GameObject) && track.Binding.propertyName == "m_IsActive")
                    {
                        // GetFloatValue não lê m_IsActive do próprio objeto de onde parte.
                        track.DefaultFloat = track.Target.activeSelf ? 1f : 0f;
                        track.Valid = true;
                    }
                    else
                    {
                        track.Valid = AnimationUtility.GetFloatValue(track.Target, track.Binding, out track.DefaultFloat);
                    }
                }
                if (track.Valid && track.Direct)
                {
                    RecordDirect(session, track);
                }
                if (!track.Valid)
                {
                    var path = AnimationUtility.CalculateTransformPath(track.Target.transform, session.Target.Avatar);
                    session.Missing.Add((path == "" ? "(root)" : path) + " · " + track.Binding.type.Name + "." + track.Binding.propertyName);
                }
            }

            if (session.NeedsHumanoid)
            {
                session.Handler.GetHumanPose(ref session.RestPose);
                if (session.Hips != null)
                {
                    session.HipsPosition = session.Hips.position;
                    session.HipsRotation = session.Hips.rotation;
                }
                RecordHumanBones(session, session.Target.Animator);
            }
            session.Prepared = true;
        }

        internal static bool IsDirectTransformProperty(string property)
        {
            return property.StartsWith("m_LocalPosition.", StringComparison.Ordinal)
                || property.StartsWith("m_LocalScale.", StringComparison.Ordinal)
                || property.StartsWith("m_LocalRotation.", StringComparison.Ordinal);
        }

        // Registrar antes de escrever faz o AnimationMode devolver o valor original ao parar.
        internal static void RecordDirect(Session target, Track track)
        {
            var owner = track.Binding.type == typeof(GameObject) ? (Object)track.Target : track.Target.transform;
            var properties = track.Binding.propertyName.StartsWith("m_LocalRotation.", StringComparison.Ordinal)
                ? new[] { "m_LocalRotation.x", "m_LocalRotation.y", "m_LocalRotation.z", "m_LocalRotation.w" }
                : new[] { track.Binding.propertyName };
            foreach (var property in properties)
            {
                var local = track.Binding;
                local.propertyName = property;
                var value = track.DefaultFloat;
                if (track.Binding.type != typeof(GameObject))
                {
                    AnimationUtility.GetFloatValue(track.Target, local, out value);
                }
                var binding = local;
                binding.path = track.AvatarPath;
                var modification = new PropertyModification { target = owner, propertyPath = property, value = value.ToString("R", CultureInfo.InvariantCulture) };
                target.Records.Add((binding, modification));
            }
        }

        internal void ApplyDirect()
        {
            foreach (var track in session.Tracks)
            {
                if (!track.Direct || !track.Valid || track.Target == null)
                {
                    continue;
                }
                var property = track.Binding.propertyName;
                if (track.Binding.type == typeof(GameObject))
                {
                    var active = track.Value > 0.5f;
                    if (track.Target.activeSelf != active)
                    {
                        track.Target.SetActive(active);
                    }
                    continue;
                }
                WriteTransform(track.Target.transform, new[] { track });
            }
        }

        // Escreve direto valores que o AnimationMode já registrou: ao parar, eles voltam ao original do mesmo jeito.
        internal static void WriteTransform(Transform transform, IEnumerable<Track> tracks)
        {
            var position = transform.localPosition;
            var rotation = transform.localRotation;
            var scale = transform.localScale;
            var euler = transform.localEulerAngles;
            bool movedRotation = false, movedEuler = false;
            foreach (var track in tracks)
            {
                if (!track.Valid)
                {
                    continue;
                }
                var property = track.Binding.propertyName;
                var axis = property[property.Length - 1];
                if (property.StartsWith("m_LocalPosition.", StringComparison.Ordinal))
                {
                    position = WithAxis(position, axis, track.Value);
                }
                else if (property.StartsWith("m_LocalScale.", StringComparison.Ordinal))
                {
                    scale = WithAxis(scale, axis, track.Value);
                }
                else if (property.StartsWith("m_LocalRotation.", StringComparison.Ordinal))
                {
                    rotation[axis == 'x' ? 0 : axis == 'y' ? 1 : axis == 'z' ? 2 : 3] = track.Value;
                    movedRotation = true;
                }
                else if (property.StartsWith("localEulerAngles", StringComparison.Ordinal))
                {
                    euler = WithAxis(euler, axis, track.Value);
                    movedEuler = true;
                }
            }
            transform.localPosition = position;
            transform.localScale = scale;
            if (movedEuler)
            {
                transform.localEulerAngles = euler;
            }
            else if (movedRotation)
            {
                transform.localRotation = rotation.normalized;
            }
        }

        // ---------------------------------------------------------------- Convivência com a simulação de PhysBones

        internal bool IsActive
        {
            get { return session != null && session.Prepared && OwnsPreviewMode; }
        }

        // Antes de a simulação ler ou escrever a pose de repouso: tudo volta ao original, e o preview se reaplica
        // sozinho no próximo tick.
        internal void Suspend()
        {
            StopPreviewMode();
        }

        // Chamado pela simulação no começo de cada passo, para os PhysBones simularem por cima da pose animada.
        internal void SampleForPhysBones()
        {
            if (IsActive)
            {
                Sample();
            }
        }

        internal void ReapplyAnimated(Transform transform)
        {
            if (IsActive && session.TransformTracks.TryGetValue(transform, out var tracks))
            {
                WriteTransform(transform, tracks);
            }
        }

        internal bool DrivesHumanoid(Animator animator)
        {
            return animator != null && IsActive && session.NeedsHumanoid && session.Target.Animator == animator;
        }

        internal static Vector3 WithAxis(Vector3 vector, char axis, float value)
        {
            vector[axis == 'x' ? 0 : axis == 'y' ? 1 : 2] = value;
            return vector;
        }

        // O HumanPoseHandler escreve direto nos ossos; registrar antes faz o AnimationMode devolvê-los ao parar.
        internal static void RecordHumanBones(Session target, Animator animator)
        {
            string[] properties = { "m_LocalPosition.x", "m_LocalPosition.y", "m_LocalPosition.z", "m_LocalRotation.x", "m_LocalRotation.y", "m_LocalRotation.z", "m_LocalRotation.w" };
            for (var bone = HumanBodyBones.Hips; bone < HumanBodyBones.LastBone; bone++)
            {
                var transform = animator.GetBoneTransform(bone);
                if (transform == null)
                {
                    continue;
                }
                var path = AnimationUtility.CalculateTransformPath(transform, animator.transform);
                var position = transform.localPosition;
                var rotation = transform.localRotation;
                float[] values = { position.x, position.y, position.z, rotation.x, rotation.y, rotation.z, rotation.w };
                for (var i = 0; i < properties.Length; i++)
                {
                    var modification = new PropertyModification
                    {
                        target = transform,
                        propertyPath = properties[i],
                        value = values[i].ToString("R", CultureInfo.InvariantCulture),
                    };
                    target.Records.Add((EditorCurveBinding.FloatCurve(path, typeof(Transform), properties[i]), modification));
                }
            }
        }

        // ---------------------------------------------------------------- AnimationMode

        internal bool OwnsPreviewMode
        {
            get { return driver != null && AnimationMode.InAnimationMode(driver); }
        }

        internal bool StartPreviewMode()
        {
            if (OwnsPreviewMode)
            {
                blockedByOtherTool = false;
                return true;
            }
            // A janela Animation, a Timeline e outros previews usam o mesmo AnimationMode: não briga com eles.
            if (AnimationMode.InAnimationMode())
            {
                blockedByOtherTool = true;
                return false;
            }
            blockedByOtherTool = false;
            if (driver == null)
            {
                driver = ScriptableObject.CreateInstance<AnimationModeDriver>();
                driver.name = "Dosker Animator Preview";
                driver.hideFlags = HideFlags.HideAndDontSave;
            }
            AnimationMode.StartAnimationMode(driver);
            if (session != null)
            {
                session.Prepared = false;
            }
            return true;
        }

        internal void StopPreviewMode()
        {
            if (OwnsPreviewMode)
            {
                AnimationMode.StopAnimationMode(driver);
                SceneView.RepaintAll();
            }
            if (session != null)
            {
                session.Prepared = false;
            }
        }

        // ---------------------------------------------------------------- Tick

        internal void Tick()
        {
            var now = EditorApplication.timeSinceStartup;
            if (now - lastTick < TickInterval)
            {
                return;
            }
            var delta = Mathf.Min((float)(now - lastTick), MaxStep);
            lastTick = now;

            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling)
            {
                StopPreviewMode();
                return;
            }

            var poll = now - lastPoll >= PollInterval;
            if (poll)
            {
                lastPoll = now;
                // O Animator não avisa quando troca de controller; a seleção pode continuar a mesma.
                if (!lockSelection && selectedMotion == null && AnimatorWindowController() != selectedController)
                {
                    selectionDirty = true;
                }
            }
            if (selectionDirty)
            {
                ResolveSelection();
            }
            if (targetsDirty)
            {
                RefreshTargets();
            }

            if (!previewOn)
            {
                if (session != null || OwnsPreviewMode)
                {
                    EndSession();
                    StopPreviewMode();
                }
                return;
            }

            if (!SessionMatchesSelection() || session != null && !session.Target.IsAlive)
            {
                if (chosenTarget != null && !chosenTarget.IsAlive)
                {
                    RefreshTargets();
                }
                BeginSession();
            }
            else if (poll && ComputeSignature(session.Controller, session.Motion) != session.Signature)
            {
                // O clip ou a blend tree mudou: lê as curvas de novo.
                BeginSession();
            }
            if (session == null)
            {
                StopPreviewMode();
                return;
            }
            if (session.Groups.Any(group => group.Target == null))
            {
                targetsDirty = true;
                BeginSession();
                return;
            }

            if (!StartPreviewMode())
            {
                RepaintThrottled(now);
                return;
            }
            if (!session.Prepared)
            {
                PrepareSession();
                sampleRequested = true;
            }

            var changed = FollowAnimatorValues();
            if (playing && stateLength > 0f)
            {
                var stateSpeed = session.State != null ? Mathf.Abs(session.State.speed) : 1f;
                normalizedTime += delta * speed * stateSpeed / stateLength;
                normalizedTime = loop ? Mathf.Repeat(normalizedTime, 1f) : Mathf.Clamp01(normalizedTime);
                changed = true;
            }
            if (Owner != null && Owner.IsSimulationActive)
            {
                // A simulação amostra no começo de cada passo; amostrar aqui também desfaria o resultado dela.
                sampleRequested = false;
            }
            else if (changed || sampleRequested)
            {
                sampleRequested = false;
                Sample();
                SceneView.RepaintAll();
            }
            RepaintThrottled(now);
        }

        internal void RepaintThrottled(double now)
        {
            if (now - lastRepaint >= RepaintInterval)
            {
                lastRepaint = now;
                Owner?.Repaint();
            }
        }

        // O slider da blend tree no Animator: o preview acompanha quando o valor muda lá.
        internal bool FollowAnimatorValues()
        {
            if (!(session?.Motion is BlendTree tree))
            {
                return false;
            }
            var ranges = new Dictionary<string, Vector2>();
            CollectParameters(tree, ranges, new HashSet<BlendTree>());
            var changed = false;
            foreach (var name in ranges.Keys)
            {
                var value = ReadAnimatorValue(tree, name);
                if (!parameterValues.ContainsKey(name))
                {
                    parameterValues[name] = value;
                    changed = true;
                }
                if (followAnimator && (!animatorValues.TryGetValue(name, out var previous) || !Mathf.Approximately(previous, value)))
                {
                    parameterValues[name] = value;
                    changed = true;
                }
                animatorValues[name] = value;
            }
            return changed;
        }

        internal float ReadAnimatorValue(BlendTree tree, string name)
        {
            if (getInputBlendValue != null)
            {
                try
                {
                    return (float)getInputBlendValue.Invoke(tree, new object[] { name });
                }
                catch (Exception)
                {
                    getInputBlendValue = null;
                }
            }
            var parameter = session?.Controller?.parameters.FirstOrDefault(item => item.name == name);
            return parameter?.defaultFloat ?? 0f;
        }

        internal void Sample()
        {
            weights.Clear();
            if (session.Evaluator != null)
            {
                stateLength = session.Evaluator.Evaluate(normalizedTime, parameterValues, weights);
            }
            else if (session.Motion is AnimationClip single)
            {
                weights.Add((single, 1f));
                stateLength = single.length;
            }

            foreach (var track in session.Tracks)
            {
                track.Value = 0f;
                track.ObjectValue = track.DefaultObject;
                track.Covered = 0f;
                track.BestWeight = 0f;
            }
            if (muscleSums == null)
            {
                muscleSums = new float[HumanTrait.MuscleCount];
            }
            Array.Clear(muscleSums, 0, muscleSums.Length);

            var total = 0f;
            foreach (var (clip, weight) in weights)
            {
                if (weight <= 0f || !session.Clips.TryGetValue(clip, out var data))
                {
                    continue;
                }
                total += weight;
                var time = normalizedTime * clip.length;
                foreach (var (index, curve) in data.Floats)
                {
                    var track = session.Tracks[index];
                    if (curve != null)
                    {
                        // Como o mixer do Unity: o que um clip não anima entra com o valor padrão.
                        track.Value += weight * (curve.Evaluate(time) * track.Scale - track.DefaultFloat);
                    }
                }
                foreach (var (index, keys) in data.Objects)
                {
                    var track = session.Tracks[index];
                    track.Covered += weight;
                    if (weight > track.BestWeight && keys != null && keys.Length > 0)
                    {
                        track.BestWeight = weight;
                        track.ObjectValue = SampleObject(keys, time);
                    }
                }
                foreach (var (muscle, curve) in data.Muscles)
                {
                    if (curve != null)
                    {
                        muscleSums[muscle] += weight * (curve.Evaluate(time) - session.RestPose.muscles[muscle]);
                    }
                }
            }
            foreach (var track in session.Tracks)
            {
                if (track.IsObject)
                {
                    // O valor padrão também disputa: vence quem tiver mais peso.
                    if (total - track.Covered > track.BestWeight)
                    {
                        track.ObjectValue = track.DefaultObject;
                    }
                }
                else
                {
                    track.Value += track.DefaultFloat;
                }
            }
            Apply();
        }

        internal static Object SampleObject(ObjectReferenceKeyframe[] keys, float time)
        {
            var value = keys[0].value;
            foreach (var key in keys)
            {
                if (key.time > time + 1e-5f)
                {
                    break;
                }
                value = key.value;
            }
            return value;
        }

        internal void Apply()
        {
            foreach (var group in session.Groups)
            {
                group.Clip.ClearCurves();
                var floats = group.Floats.Where(index => session.Tracks[index].Valid).ToArray();
                if (floats.Length > 0)
                {
                    AnimationUtility.SetEditorCurves(
                        group.Clip,
                        floats.Select(index => session.Tracks[index].Binding).ToArray(),
                        floats.Select(index => AnimationCurve.Constant(0f, 0f, session.Tracks[index].Value)).ToArray());
                }
                var objects = group.Objects.Where(index => session.Tracks[index].Valid).ToArray();
                if (objects.Length > 0)
                {
                    AnimationUtility.SetObjectReferenceCurves(
                        group.Clip,
                        objects.Select(index => session.Tracks[index].Binding).ToArray(),
                        objects.Select(index => new[] { new ObjectReferenceKeyframe { time = 0f, value = session.Tracks[index].ObjectValue } }).ToArray());
                }
            }

            AnimationMode.BeginSampling();
            try
            {
                foreach (var (binding, original) in session.Records)
                {
                    AnimationMode.AddPropertyModification(binding, original, true);
                }
                foreach (var group in session.Groups)
                {
                    if (group.Floats.Count + group.Objects.Count > 0)
                    {
                        AnimationMode.SampleAnimationClip(group.Target, group.Clip, 0f);
                    }
                }
            }
            finally
            {
                AnimationMode.EndSampling();
            }
            // Depois do EndSampling, que desfaz o que não foi amostrado por clip.
            ApplyHumanPose();
            ApplyDirect();
        }

        // Músculos animados por cima da pose da cena; o resto do corpo e o quadril ficam onde estavam.
        internal void ApplyHumanPose()
        {
            if (!session.NeedsHumanoid)
            {
                return;
            }
            var pose = session.RestPose;
            pose.muscles = (float[])session.RestPose.muscles.Clone();
            foreach (var muscle in session.Muscles)
            {
                pose.muscles[muscle] = session.RestPose.muscles[muscle] + muscleSums[muscle];
            }
            session.Handler.SetHumanPose(ref pose);
            if (session.Hips != null)
            {
                session.Hips.SetPositionAndRotation(session.HipsPosition, session.HipsRotation);
            }
        }

        // ---------------------------------------------------------------- UI

        internal enum PreviewStatus
        {
            Off,
            NoSdk,
            NoSelection,
            NoTarget,
            Blocked,
            Error,
            Paused,
            Live,
        }

        internal PreviewStatus Status
        {
            get
            {
                if (descriptorType == null)
                {
                    return PreviewStatus.NoSdk;
                }
                if (!previewOn)
                {
                    return PreviewStatus.Off;
                }
                if (selectedMotion == null)
                {
                    return PreviewStatus.NoSelection;
                }
                if (chosenTarget == null)
                {
                    return PreviewStatus.NoTarget;
                }
                if (lastError != null)
                {
                    return PreviewStatus.Error;
                }
                if (blockedByOtherTool)
                {
                    return PreviewStatus.Blocked;
                }
                return playing ? PreviewStatus.Live : PreviewStatus.Paused;
            }
        }

        // Botão da barra do topo, ao lado de Simular PhysBones. Ligar o Animator liga a simulação junto.
        internal void DrawToggleButton()
        {
            var label = previewOn ? Tr("Parar Animator", "Stop Animator") : "Animator";
            var color = previewOn ? DoskerEditorUi.Red : DoskerEditorUi.Purple;
            if (!DoskerEditorUi.PrimaryButton(label, previewOn ? "PauseButton" : "AnimatorController Icon", color, 30f, GUILayout.ExpandWidth(true)))
            {
                return;
            }
            previewOn = !previewOn;
            selectionDirty = true;
            targetsDirty = true;
            if (!previewOn)
            {
                EndSession();
                StopPreviewMode();
            }
            else if (Owner != null && !Owner.simulatePhysBones)
            {
                Owner.SetSimulation(true);
            }
        }

        // Linha de tempo na barra do topo, só com o Animator ligado.
        internal void DrawTransport()
        {
            if (!previewOn)
            {
                return;
            }
            EditorGUILayout.Space(4f);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (DoskerEditorUi.IconButton(playing ? "PauseButton" : "PlayButton", playing ? "❚❚" : "▶", playing ? Tr("Pausar o Animator", "Pause the Animator") : Tr("Tocar o Animator", "Play the Animator"), 22f))
                {
                    playing = !playing;
                }
                EditorGUI.BeginChangeCheck();
                var time = GUILayout.HorizontalSlider(normalizedTime, 0f, 1f, GUILayout.ExpandWidth(true), GUILayout.Height(20f));
                if (EditorGUI.EndChangeCheck())
                {
                    normalizedTime = time;
                    playing = false;
                    sampleRequested = true;
                }
                var seconds = normalizedTime * stateLength;
                GUILayout.Label(string.Format(CultureInfo.InvariantCulture, "{0:0.00} / {1:0.00} s", seconds, stateLength), EditorStyles.miniLabel, GUILayout.Width(84f));
            }
            EditorGUILayout.Space(2f);
            using (new EditorGUILayout.HorizontalScope())
            {
                DrawStatusPill();
                lockSelection = DoskerEditorUi.ChipToggle(lockSelection, Tr("Travar", "Lock"), lockSelection ? "LockIcon-On" : "LockIcon", DoskerEditorUi.Yellow,
                    Tr("Continua no preview atual mesmo selecionando outro estado ou clip.", "Keeps the current preview even when you select another state or clip."));
                loop = DoskerEditorUi.ChipToggle(loop, Tr("Repetir", "Loop"), "preAudioLoopOff", DoskerEditorUi.Cyan,
                    Tr("Volta ao começo ao chegar no fim.", "Starts over when it reaches the end."));
                followAnimator = DoskerEditorUi.ChipToggle(followAnimator, Tr("Seguir Animator", "Follow Animator"), "AnimatorController Icon", DoskerEditorUi.Purple,
                    Tr("Os parâmetros acompanham o slider da blend tree na janela Animator.", "Parameters follow the blend tree slider in the Animator window."));
                speed = EditorGUILayout.Slider(speed, 0f, 2f, GUILayout.MinWidth(110f));
            }
        }

        // O cartão do Animator, o primeiro da lista.
        internal void DrawCard()
        {
            if (descriptorType == null)
            {
                return;
            }
            var kind = selectedMotion is BlendTree tree ? "Blend Tree · " + tree.blendType : selectedMotion is AnimationClip ? "Clip" : null;
            using (var card = new DoskerEditorUi.CardScope("Vrc.Animator", "Animator", "AnimatorController Icon", DoskerEditorUi.Purple, kind))
            {
                if (!card.Expanded)
                {
                    return;
                }
                DrawBanners();
                DrawSelectionCard();
                DrawTargetCard();
                if (session?.Motion is BlendTree blendTree)
                {
                    DrawBlendTreeCard(blendTree);
                }
                DrawPropertiesCard();
            }
        }

        internal static void SectionHeader(string title, string detail = null)
        {
            EditorGUILayout.Space(4f);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(title, EditorStyles.miniBoldLabel);
                if (!string.IsNullOrEmpty(detail))
                {
                    GUILayout.FlexibleSpace();
                    GUILayout.Label(detail, EditorStyles.miniLabel);
                }
            }
        }

        internal void DrawStatusPill()
        {
            string text;
            var color = DoskerEditorUi.Neutral;
            switch (Status)
            {
                case PreviewStatus.Live:
                    text = Tr("Ao vivo", "Live");
                    color = DoskerEditorUi.Green;
                    break;
                case PreviewStatus.Paused:
                    text = Tr("Pausado", "Paused");
                    color = DoskerEditorUi.Cyan;
                    break;
                case PreviewStatus.NoSelection:
                    text = Tr("Aguardando seleção", "Waiting for a selection");
                    color = DoskerEditorUi.Yellow;
                    break;
                case PreviewStatus.NoTarget:
                    text = Tr("Sem avatar", "No avatar");
                    color = DoskerEditorUi.Yellow;
                    break;
                case PreviewStatus.Blocked:
                    text = Tr("Em uso por outro preview", "In use by another preview");
                    color = DoskerEditorUi.Yellow;
                    break;
                case PreviewStatus.Error:
                    text = Tr("Erro", "Error");
                    color = DoskerEditorUi.Red;
                    break;
                default:
                    text = Tr("Desligado", "Off");
                    break;
            }
            DoskerEditorUi.StatusPill(text, color, 22f);
        }

        internal void DrawBanners()
        {
            switch (Status)
            {
                case PreviewStatus.Off:
                    DoskerEditorUi.Banner(Tr(
                        "Selecione um estado, blend tree ou clip na janela Animator e ligue o Animator na barra acima. A animação aparece no avatar que usa o controller, por um Full Controller do VRCFury ou pelo Avatar Descriptor. Tudo volta ao parar.",
                        "Select a state, blend tree or clip in the Animator window and turn the Animator on in the bar above. The animation shows on the avatar that uses the controller, through a VRCFury Full Controller or the Avatar Descriptor. Everything goes back when it stops."),
                        DoskerEditorUi.BannerKind.Info);
                    break;
                case PreviewStatus.NoSelection:
                    DoskerEditorUi.Banner(Tr("Selecione um estado com animação, uma blend tree ou um clip no Animator.", "Select a state with a motion, a blend tree or a clip in the Animator."), DoskerEditorUi.BannerKind.Info);
                    break;
                case PreviewStatus.NoTarget:
                    DoskerEditorUi.Banner(selectedController == null
                        ? Tr("Não foi possível saber de qual controller vem a seleção. Abra o controller na janela Animator.", "Couldn't tell which controller the selection comes from. Open the controller in the Animator window.")
                        : Tr("Nenhum avatar da cena usa ", "No avatar in the scene uses ") + selectedController.name + Tr(" num Full Controller do VRCFury ou no Avatar Descriptor.", " in a VRCFury Full Controller or in the Avatar Descriptor."),
                        DoskerEditorUi.BannerKind.Warning);
                    break;
                case PreviewStatus.Blocked:
                    DoskerEditorUi.Banner(Tr(
                        "Outro preview do Unity (a janela Animation, a Timeline ou outra ferramenta) está usando o modo de animação. Desligue o preview dele para continuar.",
                        "Another Unity preview (the Animation window, the Timeline or another tool) is using animation mode. Turn its preview off to continue."),
                        DoskerEditorUi.BannerKind.Warning);
                    break;
                case PreviewStatus.Error:
                    DoskerEditorUi.Banner(Tr("Não foi possível montar o preview: ", "Couldn't build the preview: ") + lastError, DoskerEditorUi.BannerKind.Error);
                    break;
            }
            if (Status == PreviewStatus.Live || Status == PreviewStatus.Paused)
            {
                DoskerEditorUi.Banner(Tr(
                    "Propriedades animadas ficam vermelhas no Inspector e voltam ao valor original ao parar, salvar a cena, recompilar, entrar no Play mode ou iniciar um build do SDK. Edições nelas não ficam.",
                    "Animated properties turn red in the Inspector and go back to their original value when you stop, save the scene, recompile, enter Play mode or start an SDK build. Edits to them don't stay."),
                    DoskerEditorUi.BannerKind.Info);
            }
        }

        internal static void ObjectButton(string label, Object item, Type type)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(label, GUILayout.Width(90f));
                var content = item != null ? EditorGUIUtility.ObjectContent(item, type) : new GUIContent("—");
                if (GUILayout.Button(content, EditorStyles.objectField, GUILayout.Height(18f)) && item != null)
                {
                    EditorGUIUtility.PingObject(item);
                }
            }
        }

        internal void DrawSelectionCard()
        {
            using (new EditorGUILayout.VerticalScope())
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    ObjectButton("Controller", selectedController, typeof(AnimatorController));
                    if (GUILayout.Button(DoskerEditorUi.Content("", "Refresh", Tr("Procurar o avatar e reler a animação", "Find the avatar and read the animation again")), GUILayout.Width(24f), GUILayout.Height(18f)))
                    {
                        selectionDirty = true;
                        targetsDirty = true;
                        EndSession();
                    }
                }
                ObjectButton(Tr("Estado", "State"), selectedState, typeof(AnimatorState));
                ObjectButton(Tr("Animação", "Motion"), selectedMotion, typeof(Motion));
                if (session != null && session.Controller != null && session.LayerIndex >= 0 && session.LayerIndex < session.Controller.layers.Length)
                {
                    var layer = session.Controller.layers[session.LayerIndex];
                    var mask = layer.avatarMask != null ? " · " + Tr("máscara ", "mask ") + layer.avatarMask.name : "";
                    EditorGUILayout.LabelField(Tr("Layer", "Layer"), layer.name + mask, EditorStyles.miniLabel);
                }
            }
        }

        internal void DrawTargetCard()
        {
            var badge = targets.Count > 1 ? targets.Count.ToString(CultureInfo.InvariantCulture) : null;
            using (new EditorGUILayout.VerticalScope())
            {
                SectionHeader("Avatar", badge);
                if (targets.Count == 0)
                {
                    EditorGUILayout.LabelField(Tr("Nenhum uso deste controller na cena.", "This controller isn't used in the scene."), EditorStyles.wordWrappedMiniLabel);
                    return;
                }
                var index = Mathf.Max(0, targets.IndexOf(chosenTarget));
                var next = EditorGUILayout.Popup(index, targets.Select(target => target.Label.Replace("/", "∕")).ToArray());
                if (next != index)
                {
                    chosenTarget = targets[next];
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(Tr("Selecionar componente", "Select component"), EditorStyles.miniButton) && chosenTarget?.Source != null)
                    {
                        EditorGUIUtility.PingObject(chosenTarget.Source);
                    }
                    if (chosenTarget != null && chosenTarget.FromVrcfury)
                    {
                        var rules = chosenTarget.Rewrites.Count;
                        var text = Tr("Caminhos como no build do VRCFury", "Paths as in the VRCFury build")
                            + (rules > 0 ? " · " + rules + Tr(" regra(s) de rewrite", " rewrite rule(s)") : "")
                            + (chosenTarget.RootBindingsApplyToAvatar ? Tr(" · raiz no avatar", " · root on the avatar") : "");
                        GUILayout.Label(text, EditorStyles.miniLabel);
                    }
                }
            }
        }

        internal void DrawBlendTreeCard(BlendTree tree)
        {
            using (new EditorGUILayout.VerticalScope())
            {
                SectionHeader("Blend Tree", tree.blendType.ToString());
                var ranges = new Dictionary<string, Vector2>();
                CollectParameters(tree, ranges, new HashSet<BlendTree>());
                foreach (var pair in ranges)
                {
                    var value = parameterValues.TryGetValue(pair.Key, out var current) ? current : 0f;
                    var min = Mathf.Min(pair.Value.x, value);
                    var max = Mathf.Max(pair.Value.y, value);
                    if (Mathf.Approximately(min, max))
                    {
                        max = min + 1f;
                    }
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUI.BeginChangeCheck();
                        var next = EditorGUILayout.Slider(pair.Key, value, min, max);
                        if (EditorGUI.EndChangeCheck())
                        {
                            parameterValues[pair.Key] = next;
                            sampleRequested = true;
                        }
                        var animatorValue = animatorValues.TryGetValue(pair.Key, out var fromAnimator) ? fromAnimator : value;
                        using (new EditorGUI.DisabledScope(Mathf.Approximately(animatorValue, value)))
                        {
                            if (GUILayout.Button(DoskerEditorUi.Content("", "Refresh", Tr("Voltar ao valor do Animator", "Back to the Animator value")), GUILayout.Width(24f), GUILayout.Height(18f)))
                            {
                                parameterValues[pair.Key] = animatorValue;
                                sampleRequested = true;
                                GUIUtility.keyboardControl = 0;
                            }
                        }
                    }
                }

                if (tree.blendType == BlendTreeType.Simple1D)
                {
                    DrawBlendBar(tree);
                }
                else if (tree.blendType != BlendTreeType.Direct)
                {
                    DrawBlendPad(tree);
                }
                DrawWeights();
            }
        }

        internal float ChildWeight(Motion motion)
        {
            if (motion == null)
            {
                return 0f;
            }
            var clips = new HashSet<AnimationClip>();
            CollectClips(motion, clips, new HashSet<BlendTree>());
            return weights.Where(item => clips.Contains(item.Clip)).Sum(item => item.Weight);
        }

        internal void DrawBlendBar(BlendTree tree)
        {
            var children = tree.children;
            if (children.Length == 0)
            {
                return;
            }
            var rect = GUILayoutUtility.GetRect(1f, 34f, GUILayout.ExpandWidth(true));
            rect = new Rect(rect.x + 8f, rect.y + 6f, rect.width - 16f, rect.height - 12f);
            var value = parameterValues.TryGetValue(tree.blendParameter, out var current) ? current : 0f;
            var min = Mathf.Min(children.Min(c => c.threshold), value);
            var max = Mathf.Max(children.Max(c => c.threshold), value);
            if (Mathf.Approximately(min, max))
            {
                max = min + 1f;
            }
            float ToX(float v) => rect.x + Mathf.InverseLerp(min, max, v) * rect.width;

            HandleDrag(rect, position => parameterValues[tree.blendParameter] = Mathf.Lerp(min, max, Mathf.InverseLerp(rect.x, rect.xMax, position.x)));
            if (Event.current.type != EventType.Repaint)
            {
                return;
            }
            EditorGUI.DrawRect(new Rect(rect.x, rect.center.y - 1f, rect.width, 2f), DoskerEditorUi.Divider);
            foreach (var child in children)
            {
                var weight = Mathf.Clamp01(ChildWeight(child.motion));
                var size = 6f + 8f * weight;
                var x = ToX(child.threshold);
                EditorGUI.DrawRect(new Rect(x - size * 0.5f, rect.center.y - size * 0.5f, size, size), Color.Lerp(DoskerEditorUi.Neutral, DoskerEditorUi.Green, weight));
            }
            var marker = ToX(value);
            EditorGUI.DrawRect(new Rect(marker - 1f, rect.y, 2f, rect.height), DoskerEditorUi.Red);
        }

        internal void DrawBlendPad(BlendTree tree)
        {
            var children = tree.children;
            if (children.Length == 0)
            {
                return;
            }
            var area = GUILayoutUtility.GetRect(1f, 180f, GUILayout.ExpandWidth(true));
            var side = Mathf.Min(area.width - 16f, area.height - 8f);
            var rect = new Rect(area.center.x - side * 0.5f, area.y + 4f, side, side);
            var point = new Vector2(
                parameterValues.TryGetValue(tree.blendParameter, out var x) ? x : 0f,
                parameterValues.TryGetValue(tree.blendParameterY, out var y) ? y : 0f);
            var min = Vector2.Min(point, new Vector2(children.Min(c => c.position.x), children.Min(c => c.position.y)));
            var max = Vector2.Max(point, new Vector2(children.Max(c => c.position.x), children.Max(c => c.position.y)));
            var span = Vector2.Max(max - min, new Vector2(0.01f, 0.01f));
            min -= span * 0.1f;
            max += span * 0.1f;
            Vector2 ToRect(Vector2 v) => new Vector2(
                rect.x + Mathf.InverseLerp(min.x, max.x, v.x) * rect.width,
                rect.yMax - Mathf.InverseLerp(min.y, max.y, v.y) * rect.height);

            HandleDrag(rect, position =>
            {
                parameterValues[tree.blendParameter] = Mathf.Lerp(min.x, max.x, Mathf.InverseLerp(rect.x, rect.xMax, position.x));
                parameterValues[tree.blendParameterY] = Mathf.Lerp(min.y, max.y, Mathf.InverseLerp(rect.yMax, rect.y, position.y));
            });
            if (Event.current.type != EventType.Repaint)
            {
                return;
            }
            EditorGUI.DrawRect(rect, new Color(0f, 0f, 0f, 0.18f));
            var origin = ToRect(Vector2.zero);
            if (rect.Contains(new Vector2(origin.x, rect.center.y)))
            {
                EditorGUI.DrawRect(new Rect(origin.x, rect.y, 1f, rect.height), DoskerEditorUi.Divider);
            }
            if (rect.Contains(new Vector2(rect.center.x, origin.y)))
            {
                EditorGUI.DrawRect(new Rect(rect.x, origin.y, rect.width, 1f), DoskerEditorUi.Divider);
            }
            foreach (var child in children)
            {
                var weight = Mathf.Clamp01(ChildWeight(child.motion));
                var size = 6f + 10f * weight;
                var center = ToRect(child.position);
                EditorGUI.DrawRect(new Rect(center.x - size * 0.5f, center.y - size * 0.5f, size, size), Color.Lerp(DoskerEditorUi.Neutral, DoskerEditorUi.Green, weight));
            }
            var marker = ToRect(point);
            EditorGUI.DrawRect(new Rect(marker.x - 6f, marker.y - 1f, 12f, 2f), DoskerEditorUi.Red);
            EditorGUI.DrawRect(new Rect(marker.x - 1f, marker.y - 6f, 2f, 12f), DoskerEditorUi.Red);
        }

        internal void HandleDrag(Rect rect, Action<Vector2> set)
        {
            var id = GUIUtility.GetControlID(FocusType.Passive, rect);
            var current = Event.current;
            switch (current.GetTypeForControl(id))
            {
                case EventType.MouseDown:
                    if (current.button == 0 && rect.Contains(current.mousePosition))
                    {
                        GUIUtility.hotControl = id;
                        set(current.mousePosition);
                        sampleRequested = true;
                        current.Use();
                    }
                    break;
                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == id)
                    {
                        set(current.mousePosition);
                        sampleRequested = true;
                        current.Use();
                    }
                    break;
                case EventType.MouseUp:
                    if (GUIUtility.hotControl == id)
                    {
                        GUIUtility.hotControl = 0;
                        current.Use();
                    }
                    break;
            }
        }

        internal void DrawWeights()
        {
            var rows = weights.Where(item => item.Weight > 0.0005f).OrderByDescending(item => item.Weight).ToList();
            if (rows.Count == 0)
            {
                return;
            }
            EditorGUILayout.Space(2f);
            EditorGUILayout.LabelField(Tr("Pesos dos clips", "Clip weights"), EditorStyles.miniBoldLabel);
            foreach (var (clip, weight) in rows.Take(MaxWeightRows))
            {
                var rect = GUILayoutUtility.GetRect(1f, 18f, GUILayout.ExpandWidth(true));
                rect.height -= 2f;
                DoskerEditorUi.ValueBar(rect, weight, clip.name + "  " + (weight * 100f).ToString("0", CultureInfo.InvariantCulture) + "%", DoskerEditorUi.Green);
            }
            if (rows.Count > MaxWeightRows)
            {
                EditorGUILayout.LabelField(Tr("… e mais ", "… and ") + (rows.Count - MaxWeightRows) + Tr(" clips", " more clips"), EditorStyles.miniLabel);
            }
        }

        internal void DrawPropertiesCard()
        {
            if (session == null)
            {
                return;
            }
            var valid = session.Tracks.Count(track => track.Valid);
            var badge = session.Missing.Count > 0 ? session.Missing.Count + Tr(" sem alvo", " missing") : valid.ToString(CultureInfo.InvariantCulture);
            using (new EditorGUILayout.VerticalScope())
            {
                SectionHeader(Tr("Propriedades", "Properties"), badge);
                EditorGUILayout.LabelField(
                    valid + Tr(" propriedades em ", " properties on ") + session.Groups.Count + Tr(" objetos", " objects")
                    + (session.Muscles.Count > 0 ? " · " + session.Muscles.Count + Tr(" músculos", " muscles") : ""),
                    EditorStyles.miniLabel);
                if (session.IgnoredMuscles > 0)
                {
                    EditorGUILayout.LabelField(
                        session.IgnoredMuscles + Tr(" curvas de músculo ignoradas (avatar não humanoide ou fora da máscara da layer).", " muscle curves ignored (non-humanoid avatar or outside the layer mask)."),
                        EditorStyles.wordWrappedMiniLabel);
                }
                if (session.Missing.Count == 0)
                {
                    return;
                }
                showMissing = EditorGUILayout.Foldout(showMissing,
                    session.Missing.Count + Tr(" caminhos sem alvo no avatar (o build também os descarta)", " paths with no target on the avatar (the build drops them too)"), true);
                if (!showMissing)
                {
                    return;
                }
                foreach (var missing in session.Missing.Take(MaxMissingRows))
                {
                    EditorGUILayout.SelectableLabel(missing, EditorStyles.miniLabel, GUILayout.Height(14f));
                }
                if (session.Missing.Count > MaxMissingRows)
                {
                    EditorGUILayout.LabelField(Tr("… e mais ", "… and ") + (session.Missing.Count - MaxMissingRows), EditorStyles.miniLabel);
                }
            }
        }
    }
}
