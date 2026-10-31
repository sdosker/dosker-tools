using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

// Visualiza componentes do VRChat SDK fora do Play mode: PhysBones (com simulação
// temporária), colliders, contacts (com cálculo de colisão/proximidade) e constraints.
// O SDK é acessado por reflexão, então o script compila mesmo sem o SDK no projeto.
public sealed class VrcEditModePreviewWindow : EditorWindow
{
    private const float SimulationStep = 1f / 60f;
    private const int MaxParticlesPerChain = 512;
    private const int GrabSolverIterations = 8;

    // Layout de Packages/com.vrchat.base/ShaderLibrary/VRCContacts.cginc.
    private const int MaxShaderTargets = 8;
    private const int MaxShaderRenderers = 4;
    private const int ShaderBufferSize = MaxShaderTargets + 1;

    private static readonly Dictionary<(Type, string), FieldInfo> FieldCache = new Dictionary<(Type, string), FieldInfo>();

    private static Type physBoneType;
    private static Type colliderType;
    private static Type contactType;
    private static Type receiverType;
    private static Type constraintType;
    private static Type avatarDescriptorType;

    private readonly List<Component> physBones = new List<Component>();
    private readonly List<Component> colliders = new List<Component>();
    private readonly List<Component> senders = new List<Component>();
    private readonly List<Component> receivers = new List<Component>();
    private readonly List<Component> constraints = new List<Component>();
    private readonly List<Chain> chains = new List<Chain>();
    private readonly HashSet<Component> simulatedBones = new HashSet<Component>();
    private readonly Dictionary<Component, ReceiverState> receiverStates = new Dictionary<Component, ReceiverState>();
    private readonly Dictionary<Component, Vector3> previousSenderPositions = new Dictionary<Component, Vector3>();
    private readonly Dictionary<Renderer, TouchedRenderer> touchedRenderers = new Dictionary<Renderer, TouchedRenderer>();
    private readonly Dictionary<Transform, int> playerIds = new Dictionary<Transform, int>();
    private MaterialPropertyBlock propertyBlock;
    private double lastContactTime;
    private bool writeMaterialContacts = true;

    private bool showPhysBones = true;
    private bool showPhysBoneLimits = true;
    private bool showColliders = true;
    private bool showContacts = true;
    private bool showConstraints = true;
    private bool showConstraintGhosts = true;
    private bool showLabels = true;
    private bool drawThroughGeometry = true;
    private bool onlySelection;
    private bool simulatePhysBones;
    private bool allowGrab = true;
    private bool lockMode;
    private bool lockTracking;
    private bool lockMoved;
    private int lockUndoGroup;
    private bool liveContacts = true;
    private float handleOpacity = 0.8f;
    private Vector2 scroll;
    private bool componentsDirty = true;
    private double nextComponentRefresh;
    private double lastSimulationTime;
    private float simulationAccumulator;
    private Chain grabbedChain;
    private int grabbedParticle = -1;
    private Vector3 grabbedPosition;

    private sealed class Particle
    {
        public Transform Transform;
        public int Parent;
        public readonly List<int> Children = new List<int>();
        public bool Virtual;
        public Vector3 EndOffset;
        public Vector3 RestLocalPosition;
        public Quaternion RestLocalRotation;
        public Vector3 RestPosition;
        public Vector3 Position;
        public Vector3 PreviousPosition;
        public float Ratio;
        public float Length;
    }

    private sealed class Chain
    {
        public Component Bone;
        public Transform Root;
        public readonly List<Particle> Particles = new List<Particle>();
        public Vector3 LastRootPosition;
        public bool Applied;
        public float Angle;
        public bool Grabbed;
    }

    private sealed class ReceiverState
    {
        public bool Colliding;
        public float Value;
        public float Proximity;
        public float PreviousProximity;
        public double EnterTime = -10.0;
        public readonly List<string> Senders = new List<string>();
        public readonly List<ContactHit> Hits = new List<ContactHit>();
        public int SenderType;
        public Vector3 SenderLocalPosition;
        public Vector3 SenderLossyScale;
        public Vector3 SenderVelocity;
        public float PointSeparation = -1f;
        public float ProximitySpeed;
        public readonly HashSet<string> MatchedTags = new HashSet<string>();
        public string ShaderName;
        public int WrittenRenderers;
    }

    private struct ContactHit
    {
        public Component Sender;
        public Shape Shape;
        public float Proximity;
        public float Gap;
        public uint TagBits;
        public bool SameAvatar;
    }

    private struct Shape
    {
        public int Kind; // 0 esfera, 1 cápsula, 2 caixa, 3 plano
        public Vector3 Center;
        public Quaternion Rotation;
        public Vector3 A;
        public Vector3 B;
        public float Radius;
        public float Height;
        public Vector3 HalfSize;
        public bool Inside;
    }

    private sealed class TouchedRenderer
    {
        public bool WasEmpty;
        public readonly HashSet<string> Names = new HashSet<string>();
    }

    [MenuItem("Tools/Dosker Tools/VRChat Edit-Mode Preview")]
    public static void OpenWindow()
    {
        var window = GetWindow<VrcEditModePreviewWindow>("VRChat Edit-Mode Preview");
        window.minSize = new Vector2(320f, 300f);
        window.Show();
    }

    private void OnEnable()
    {
        ResolveTypes();
        SceneView.beforeSceneGui += HandleLockMouseDown;
        SceneView.duringSceneGui += DrawSceneGui;
        Undo.postprocessModifications += HandleLockModifications;
        EditorApplication.update += HandleEditorUpdate;
        EditorApplication.hierarchyChanged += MarkComponentsDirty;
        EditorApplication.playModeStateChanged += HandlePlayModeChanged;
        AssemblyReloadEvents.beforeAssemblyReload += RestoreAllChains;
        AssemblyReloadEvents.beforeAssemblyReload += ClearMaterialContacts;
        EditorSceneManager.sceneSaving += HandleSceneSaving;
        Selection.selectionChanged += MarkComponentsDirty;
        lastSimulationTime = EditorApplication.timeSinceStartup;
    }

    private void OnDisable()
    {
        RestoreAllChains();
        ClearMaterialContacts();
        SceneView.beforeSceneGui -= HandleLockMouseDown;
        SceneView.duringSceneGui -= DrawSceneGui;
        Undo.postprocessModifications -= HandleLockModifications;
        EditorApplication.update -= HandleEditorUpdate;
        EditorApplication.hierarchyChanged -= MarkComponentsDirty;
        EditorApplication.playModeStateChanged -= HandlePlayModeChanged;
        AssemblyReloadEvents.beforeAssemblyReload -= RestoreAllChains;
        AssemblyReloadEvents.beforeAssemblyReload -= ClearMaterialContacts;
        EditorSceneManager.sceneSaving -= HandleSceneSaving;
        Selection.selectionChanged -= MarkComponentsDirty;
        SceneView.RepaintAll();
    }

    private void MarkComponentsDirty()
    {
        componentsDirty = true;
    }

    private void HandlePlayModeChanged(PlayModeStateChange change)
    {
        if (change == PlayModeStateChange.ExitingEditMode)
        {
            simulatePhysBones = false;
            lockTracking = false;
            RestoreAllChains();
            chains.Clear();
            ClearMaterialContacts();
        }
        componentsDirty = true;
    }

    // ---------------------------------------------------------------- Modo de bloqueio

    private void HandleLockMouseDown(SceneView sceneView)
    {
        var current = Event.current;
        if (!lockMode || !IsSimulationActive || current.type != EventType.MouseDown || current.button != 0 || current.alt)
        {
            return;
        }
        Undo.IncrementCurrentGroup();
        lockUndoGroup = Undo.GetCurrentGroup();
        lockTracking = true;
        lockMoved = false;
    }

    private UndoPropertyModification[] HandleLockModifications(UndoPropertyModification[] modifications)
    {
        if (lockTracking && modifications.Any(m => m.currentValue != null && m.currentValue.target is Transform))
        {
            lockMoved = true;
        }
        return modifications;
    }

    private void FinishLockDrag()
    {
        if (!lockTracking || GUIUtility.hotControl != 0)
        {
            return;
        }
        lockTracking = false;
        if (lockMoved)
        {
            // Volta pelo Undo: restaura a seleção inteira sem deixar entrada nem override.
            Undo.FlushUndoRecordObjects();
            Undo.RevertAllDownToGroup(lockUndoGroup);
        }
    }

    private void HandleSceneSaving(UnityEngine.SceneManagement.Scene scene, string path)
    {
        // Nunca salva a pose simulada: volta à pose de repouso; o próximo passo reaplica a simulação.
        RestoreAllChains();
    }

    private static void ResolveTypes()
    {
        physBoneType = FindType("VRC.Dynamics.VRCPhysBoneBase");
        colliderType = FindType("VRC.Dynamics.VRCPhysBoneColliderBase");
        contactType = FindType("VRC.Dynamics.ContactBase");
        receiverType = FindType("VRC.Dynamics.ContactReceiver");
        constraintType = FindType("VRC.Dynamics.VRCConstraintBase");
        avatarDescriptorType = FindType("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor");
    }

    private static Type FindType(string fullName)
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

    private static bool SdkAvailable
    {
        get { return physBoneType != null || contactType != null || constraintType != null; }
    }

    private bool IsSimulationActive
    {
        get { return simulatePhysBones && !EditorApplication.isPlayingOrWillChangePlaymode; }
    }

    // ---------------------------------------------------------------- UI

    private void OnGUI()
    {
        if (EditorApplication.isPlaying)
        {
            EditorGUILayout.HelpBox(
                "Em Play mode o próprio SDK executa PhysBones, contacts e constraints. Esta janela atua somente no Edit mode.",
                MessageType.Info);
            return;
        }

        if (!SdkAvailable)
        {
            EditorGUILayout.HelpBox(
                "VRChat SDK não encontrado neste projeto. Instale com.vrchat.base (e avatars/worlds) pelo VCC para usar esta janela.",
                MessageType.Warning);
            if (GUILayout.Button("Procurar novamente"))
            {
                ResolveTypes();
                componentsDirty = true;
            }
            return;
        }

        DrawSimulationBar();
        DoskerEditorUi.HorizontalLine();

        scroll = EditorGUILayout.BeginScrollView(scroll);
        EditorGUI.BeginChangeCheck();
        DrawPhysBonesCard();
        DrawCollidersCard();
        DrawContactsCard();
        DrawConstraintsCard();
        DrawDisplayCard();
        if (EditorGUI.EndChangeCheck())
        {
            componentsDirty = true;
            SceneView.RepaintAll();
        }
        EditorGUILayout.EndScrollView();
    }

    private void DrawSimulationBar()
    {
        using (DoskerEditorUi.ActionBar())
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                var simulateLabel = simulatePhysBones ? "Parar simulação" : "Simular PhysBones";
                var simulateIcon = simulatePhysBones ? "PauseButton" : "PlayButton";
                var simulateColor = simulatePhysBones ? DoskerEditorUi.Red : DoskerEditorUi.Green;
                if (DoskerEditorUi.PrimaryButton(simulateLabel, simulateIcon, simulateColor, 30f, GUILayout.ExpandWidth(true)))
                {
                    SetSimulation(!simulatePhysBones);
                }
                using (new EditorGUI.DisabledScope(!simulatePhysBones))
                {
                    if (DoskerEditorUi.IconButton("Refresh", "↻", "Reiniciar a simulação"))
                    {
                        RestartSimulation();
                    }
                }
            }

            EditorGUILayout.Space(4f);
            using (new EditorGUILayout.HorizontalScope())
            {
                var status = !simulatePhysBones ? "Desligada" : lockMode ? "Ativa · Bloqueio" : "Ativa";
                var statusColor = simulatePhysBones ? DoskerEditorUi.Green : DoskerEditorUi.Neutral;
                DoskerEditorUi.StatusPill(status, statusColor, 22f);
                using (new EditorGUI.DisabledScope(!simulatePhysBones))
                {
                    lockMode = DoskerEditorUi.ChipToggle(
                        lockMode,
                        "Bloqueio",
                        lockMode ? "LockIcon-On" : "LockIcon",
                        DoskerEditorUi.Yellow,
                        "Ao soltar o mouse, o objeto movido na Scene View volta ao estado original.");
                    allowGrab = DoskerEditorUi.ChipToggle(
                        allowGrab,
                        "Arrastar",
                        "ViewToolMove",
                        DoskerEditorUi.Cyan,
                        "Arrastar ossos pelos pontos na Scene View; os ossos anteriores acompanham.");
                }
            }
        }
    }

    private void DrawPhysBonesCard()
    {
        using (var card = new DoskerEditorUi.CardScope(
            "Vrc.PhysBones",
            "PhysBones",
            "Avatar Icon",
            DoskerEditorUi.Cyan,
            physBones.Count.ToString(),
            ref showPhysBones))
        {
            if (!card.Expanded)
            {
                return;
            }
            showPhysBoneLimits = EditorGUILayout.ToggleLeft("Limites de ângulo", showPhysBoneLimits);
            if (physBones.Count > 0 && simulatePhysBones)
            {
                EditorGUILayout.Space(2f);
                foreach (var chain in chains)
                {
                    DrawChainRow(chain);
                }
            }
        }
    }

    private void DrawCollidersCard()
    {
        using (new DoskerEditorUi.CardScope(
            "Vrc.Colliders",
            "PhysBone Colliders",
            "CapsuleCollider Icon",
            DoskerEditorUi.Yellow,
            colliders.Count.ToString(),
            ref showColliders))
        {
        }
    }

    private void DrawContactsCard()
    {
        using (var card = new DoskerEditorUi.CardScope(
            "Vrc.Contacts",
            "Contacts",
            "SphereCollider Icon",
            DoskerEditorUi.Blue,
            string.Format("{0} · {1}", senders.Count, receivers.Count),
            ref showContacts))
        {
            if (!card.Expanded)
            {
                return;
            }
            var nextLiveContacts = EditorGUILayout.ToggleLeft("Calcular colisões de contacts", liveContacts);
            using (new EditorGUI.DisabledScope(!nextLiveContacts))
            using (new EditorGUI.IndentLevelScope())
            {
                var nextWrite = EditorGUILayout.ToggleLeft("Material Contacts (Write to Shaders)", writeMaterialContacts);
                if (nextWrite != writeMaterialContacts && !nextWrite)
                {
                    ClearMaterialContacts();
                }
                writeMaterialContacts = nextWrite;
            }
            if (nextLiveContacts != liveContacts && !nextLiveContacts)
            {
                ClearMaterialContacts();
            }
            liveContacts = nextLiveContacts;

            if (receivers.Count > 0)
            {
                EditorGUILayout.Space(2f);
                foreach (var receiver in receivers)
                {
                    DrawReceiverRow(receiver);
                }
            }
        }
    }

    private void DrawConstraintsCard()
    {
        using (var card = new DoskerEditorUi.CardScope(
            "Vrc.Constraints",
            "VRC Constraints",
            "ParentConstraint Icon",
            DoskerEditorUi.Purple,
            constraints.Count.ToString(),
            ref showConstraints))
        {
            if (card.Expanded)
            {
                showConstraintGhosts = EditorGUILayout.ToggleLeft("Pose resolvida (fantasma)", showConstraintGhosts);
            }
        }
    }

    private void DrawDisplayCard()
    {
        using (var card = new DoskerEditorUi.CardScope(
            "Vrc.Display",
            "Opções de exibição",
            "SettingsIcon",
            DoskerEditorUi.Neutral,
            defaultExpanded: false))
        {
            if (!card.Expanded)
            {
                return;
            }
            showLabels = EditorGUILayout.ToggleLeft("Rótulos", showLabels);
            drawThroughGeometry = EditorGUILayout.ToggleLeft("Desenhar através da geometria", drawThroughGeometry);
            onlySelection = EditorGUILayout.ToggleLeft("Somente a hierarquia selecionada", onlySelection);
            handleOpacity = EditorGUILayout.Slider("Opacidade", handleOpacity, 0.1f, 1f);
            EditorGUILayout.HelpBox(
                "Os desenhos seguem o botão Gizmos da Scene View; os pontos de arraste continuam visíveis. " +
                "A simulação gira os ossos das cadeias apenas enquanto está ativa e restaura a pose original ao desligar, " +
                "salvar a cena, recompilar ou entrar no Play mode. Mova o avatar ou arraste os ossos para ver a reação; " +
                "ao arrastar, os ossos anteriores acompanham. Com Bloqueio ativo, o objeto movido volta ao estado original " +
                "ao soltar o mouse. É uma aproximação do solver do VRChat: valores finais devem ser confirmados no Play mode/cliente.",
                MessageType.None);
        }
    }

    private void DrawReceiverRow(Component receiver)
    {
        if (receiver == null)
        {
            return;
        }
        receiverStates.TryGetValue(receiver, out var state);
        var parameter = Field<string>(receiver, "parameter");
        var type = EnumField(receiver, "receiverType");
        var typeLabel = type == 0 ? "Constant" : type == 1 ? "OnEnter" : "Proximity";
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button(receiver.name, EditorStyles.label, GUILayout.Width(140f)))
            {
                Selection.activeObject = receiver.gameObject;
                EditorGUIUtility.PingObject(receiver.gameObject);
            }
            var rect = GUILayoutUtility.GetRect(60f, EditorGUIUtility.singleLineHeight, GUILayout.ExpandWidth(true));
            var value = state != null ? state.Value : 0f;
            var label = string.Format(
                "{0} · {1} = {2:0.00}",
                string.IsNullOrEmpty(parameter) ? "(sem parâmetro)" : parameter,
                typeLabel,
                value);
            var active = state != null && state.Colliding;
            DoskerEditorUi.ValueBar(rect, value, label, active ? DoskerEditorUi.Green : DoskerEditorUi.Blue);
        }
        if (state == null)
        {
            return;
        }
        if (state.Senders.Count > 0)
        {
            EditorGUILayout.LabelField("    ← " + string.Join(", ", state.Senders), EditorStyles.miniLabel);
        }
        if (Field(receiver, "advancedParameters", false))
        {
            var tags = Field<List<string>>(receiver, "collisionTags") ?? new List<string>();
            var tagText = string.Join(" ", tags.Select(t => string.Format("_CollisionTag_{0}={1}", t, state.MatchedTags.Contains(t) ? 1 : 0)));
            EditorGUILayout.LabelField(
                string.Format(
                    "    _SenderType={0} · _SenderLocalPosition=({1:0.000}, {2:0.000}, {3:0.000}) · _SenderLossyScale=({4:0.##}, {5:0.##}, {6:0.##})\n" +
                    "    _SenderVelocity=({7:0.00}, {8:0.00}, {9:0.00}) |{10:0.00}| · _PointSeparation={11:0.000} · _ProximitySpeed={12:0.00}\n    {13}",
                    state.SenderType,
                    state.SenderLocalPosition.x,
                    state.SenderLocalPosition.y,
                    state.SenderLocalPosition.z,
                    state.SenderLossyScale.x,
                    state.SenderLossyScale.y,
                    state.SenderLossyScale.z,
                    state.SenderVelocity.x,
                    state.SenderVelocity.y,
                    state.SenderVelocity.z,
                    state.SenderVelocity.magnitude,
                    state.PointSeparation,
                    state.ProximitySpeed,
                    tagText),
                EditorStyles.wordWrappedMiniLabel);
        }
        if (!string.IsNullOrEmpty(state.ShaderName))
        {
            var message = state.WrittenRenderers > 0
                ? string.Format(
                    "    Shader: _VRChatContact_*_{0} → {1} renderer(s) · {2} alvo(s)",
                    state.ShaderName,
                    state.WrittenRenderers,
                    Mathf.Min(state.Hits.Count, MaxShaderTargets))
                : "    Write to Shaders ativo, mas nenhum renderer foi definido no receiver.";
            EditorGUILayout.LabelField(message, EditorStyles.miniLabel);
        }
    }

    private static void DrawChainRow(Chain chain)
    {
        if (chain.Bone == null)
        {
            return;
        }
        var parameter = Field<string>(chain.Bone, "parameter");
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button(chain.Bone.name, EditorStyles.label, GUILayout.Width(140f)))
            {
                Selection.activeObject = chain.Bone.gameObject;
                EditorGUIUtility.PingObject(chain.Bone.gameObject);
            }
            var rect = GUILayoutUtility.GetRect(60f, EditorGUIUtility.singleLineHeight, GUILayout.ExpandWidth(true));
            var label = string.Format(
                "{0}_Angle = {1:0.00}{2}",
                string.IsNullOrEmpty(parameter) ? "(sem parâmetro)" : parameter,
                chain.Angle,
                chain.Grabbed ? " · _IsGrabbed" : string.Empty);
            DoskerEditorUi.ValueBar(rect, chain.Angle, label, chain.Grabbed ? DoskerEditorUi.Purple : DoskerEditorUi.Cyan);
        }
    }

    // ---------------------------------------------------------------- Atualização

    private void HandleEditorUpdate()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode || !SdkAvailable)
        {
            return;
        }

        FinishLockDrag();

        var now = EditorApplication.timeSinceStartup;
        if (componentsDirty || now >= nextComponentRefresh)
        {
            RefreshComponents();
            nextComponentRefresh = now + 1.0;
        }

        var changed = false;
        if (IsSimulationActive)
        {
            simulationAccumulator += Mathf.Min((float)(now - lastSimulationTime), 0.1f);
            var steps = 0;
            while (simulationAccumulator >= SimulationStep && steps < 4)
            {
                foreach (var chain in chains)
                {
                    StepChain(chain);
                }
                simulationAccumulator -= SimulationStep;
                steps++;
            }
            changed = steps > 0;
        }
        lastSimulationTime = now;

        if (liveContacts && receivers.Count > 0)
        {
            UpdateContacts(now);
            changed = true;
        }

        if (changed)
        {
            SceneView.RepaintAll();
            Repaint();
        }
    }

    private void RefreshComponents()
    {
        componentsDirty = false;
        CollectComponents(physBoneType, physBones);
        CollectComponents(colliderType, colliders);
        CollectComponents(constraintType, constraints);
        var contacts = new List<Component>();
        CollectComponents(contactType, contacts);
        senders.Clear();
        receivers.Clear();
        foreach (var contact in contacts)
        {
            if (receiverType != null && receiverType.IsInstanceOfType(contact))
            {
                receivers.Add(contact);
            }
            else
            {
                senders.Add(contact);
            }
        }

        if (simulatePhysBones)
        {
            if (!simulatedBones.SetEquals(physBones))
            {
                RestartSimulation();
            }
        }
    }

    private void CollectComponents(Type type, List<Component> target)
    {
        target.Clear();
        if (type == null)
        {
            return;
        }

        IEnumerable<Component> found;
        var prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
        if (prefabStage != null)
        {
            found = prefabStage.prefabContentsRoot.GetComponentsInChildren(type, false);
        }
        else
        {
            found = Object.FindObjectsByType(type, FindObjectsInactive.Exclude, FindObjectsSortMode.None).OfType<Component>();
        }

        var selected = onlySelection ? Selection.transforms : null;
        foreach (var component in found)
        {
            if (component == null || (component is Behaviour behaviour && !behaviour.isActiveAndEnabled))
            {
                continue;
            }
            if (selected != null && !IsRelatedToSelection(component.transform, selected))
            {
                continue;
            }
            target.Add(component);
        }
    }

    private static bool IsRelatedToSelection(Transform transform, Transform[] selected)
    {
        foreach (var selection in selected)
        {
            if (transform.IsChildOf(selection) || selection.IsChildOf(transform))
            {
                return true;
            }
        }
        return false;
    }

    // ---------------------------------------------------------------- Reflexão

    private static FieldInfo GetField(Type type, string name)
    {
        var key = (type, name);
        if (FieldCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        FieldInfo field = null;
        for (var current = type; current != null && field == null; current = current.BaseType)
        {
            field = current.GetField(
                name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        }
        FieldCache[key] = field;
        return field;
    }

    private static T Field<T>(object target, string name, T fallback = default)
    {
        if (target == null)
        {
            return fallback;
        }
        var field = GetField(target.GetType(), name);
        if (field == null)
        {
            return fallback;
        }
        var value = field.GetValue(target);
        return value is T typed ? typed : fallback;
    }

    private static int EnumField(object target, string name)
    {
        if (target == null)
        {
            return 0;
        }
        var field = GetField(target.GetType(), name);
        var value = field != null ? field.GetValue(target) : null;
        return value != null ? Convert.ToInt32(value) : 0;
    }

    private static Transform RootOf(Component component)
    {
        var root = Field<Transform>(component, "rootTransform");
        return root != null ? root : component.transform;
    }

    private static float CurveValue(Component component, string valueName, string curveName, float ratio)
    {
        var value = Field(component, valueName, 0f);
        var curve = Field<AnimationCurve>(component, curveName);
        return curve != null && curve.length > 0 ? value * curve.Evaluate(ratio) : value;
    }

    private static float MaxScale(Transform transform)
    {
        var scale = transform.lossyScale;
        return Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));
    }

    // ---------------------------------------------------------------- Formas

    private static Shape ContactShape(Component contact)
    {
        var root = RootOf(contact);
        var scale = MaxScale(root);
        var shape = new Shape
        {
            Kind = EnumField(contact, "shapeType"),
            Center = root.TransformPoint(Field(contact, "position", Vector3.zero)),
            Rotation = root.rotation * Field(contact, "rotation", Quaternion.identity),
            Radius = Field(contact, "radius", 0f) * scale,
            Height = Field(contact, "height", 0f) * scale,
        };
        if (shape.Kind == 2)
        {
            var lossy = root.lossyScale;
            var size = Field(contact, "size", Vector3.one);
            shape.HalfSize = new Vector3(Mathf.Abs(size.x * lossy.x), Mathf.Abs(size.y * lossy.y), Mathf.Abs(size.z * lossy.z)) * 0.5f;
        }
        FinishSegment(ref shape);
        return shape;
    }

    private static Shape ColliderShape(Component collider)
    {
        var root = RootOf(collider);
        var scale = MaxScale(root);
        var kind = EnumField(collider, "shapeType");
        var shape = new Shape
        {
            Kind = kind == 2 ? 3 : kind,
            Center = root.TransformPoint(Field(collider, "position", Vector3.zero)),
            Rotation = root.rotation * Field(collider, "rotation", Quaternion.identity),
            Radius = Field(collider, "radius", 0f) * scale,
            Height = Field(collider, "height", 0f) * scale,
            Inside = Field(collider, "insideBounds", false),
        };
        FinishSegment(ref shape);
        return shape;
    }

    private static void FinishSegment(ref Shape shape)
    {
        // A altura da cápsula inclui as duas semiesferas.
        var half = shape.Kind == 1 ? Mathf.Max(0f, shape.Height * 0.5f - shape.Radius) : 0f;
        var axis = shape.Rotation * Vector3.up * half;
        shape.A = shape.Center + axis;
        shape.B = shape.Center - axis;
    }

    private static Vector3 ClosestOnSegment(Vector3 point, Vector3 a, Vector3 b)
    {
        var ab = b - a;
        var lengthSq = ab.sqrMagnitude;
        if (lengthSq < 1e-10f)
        {
            return a;
        }
        return a + ab * Mathf.Clamp01(Vector3.Dot(point - a, ab) / lengthSq);
    }

    private static void ClosestSegmentPoints(Vector3 a1, Vector3 b1, Vector3 a2, Vector3 b2, out Vector3 c1, out Vector3 c2)
    {
        var d1 = b1 - a1;
        var d2 = b2 - a2;
        var r = a1 - a2;
        var a = Vector3.Dot(d1, d1);
        var e = Vector3.Dot(d2, d2);
        var f = Vector3.Dot(d2, r);
        float s;
        float t;
        if (a <= 1e-10f && e <= 1e-10f)
        {
            c1 = a1;
            c2 = a2;
            return;
        }
        if (a <= 1e-10f)
        {
            s = 0f;
            t = Mathf.Clamp01(f / e);
        }
        else
        {
            var c = Vector3.Dot(d1, r);
            if (e <= 1e-10f)
            {
                t = 0f;
                s = Mathf.Clamp01(-c / a);
            }
            else
            {
                var b = Vector3.Dot(d1, d2);
                var denominator = a * e - b * b;
                s = denominator > 1e-10f ? Mathf.Clamp01((b * f - c * e) / denominator) : 0f;
                t = (b * s + f) / e;
                if (t < 0f)
                {
                    t = 0f;
                    s = Mathf.Clamp01(-c / a);
                }
                else if (t > 1f)
                {
                    t = 1f;
                    s = Mathf.Clamp01((b - c) / a);
                }
            }
        }
        c1 = a1 + d1 * s;
        c2 = a2 + d2 * t;
    }

    private static Vector3 ClosestOnBox(Vector3 point, Shape box)
    {
        var local = Quaternion.Inverse(box.Rotation) * (point - box.Center);
        local = Vector3.Max(-box.HalfSize, Vector3.Min(box.HalfSize, local));
        return box.Center + box.Rotation * local;
    }

    // Distância entre a superfície de "from" e o núcleo (ponto/segmento) de "core".
    private static float DistanceToCore(Shape from, Shape core, out Vector3 corePoint)
    {
        if (from.Kind == 2)
        {
            var best = float.MaxValue;
            corePoint = core.Center;
            for (var i = 0; i <= 8; i++)
            {
                var sample = Vector3.Lerp(core.A, core.B, i / 8f);
                var distance = Vector3.Distance(ClosestOnBox(sample, from), sample);
                if (distance < best)
                {
                    best = distance;
                    corePoint = sample;
                }
            }
            return best;
        }

        if (core.Kind == 2)
        {
            var closest = ClosestOnBox(ClosestOnSegment(core.Center, from.A, from.B), core);
            var onFrom = ClosestOnSegment(closest, from.A, from.B);
            corePoint = core.Center;
            return Mathf.Max(0f, Vector3.Distance(closest, onFrom) - from.Radius);
        }

        ClosestSegmentPoints(from.A, from.B, core.A, core.B, out var fromPoint, out corePoint);
        return Mathf.Max(0f, Vector3.Distance(fromPoint, corePoint) - from.Radius);
    }

    // ---------------------------------------------------------------- Contacts

    private void UpdateContacts(double now)
    {
        var deltaTime = Mathf.Max(1e-4f, (float)(now - lastContactTime));
        lastContactTime = now;
        playerIds.Clear();

        var senderShapes = new List<KeyValuePair<Component, Shape>>(senders.Count);
        foreach (var sender in senders)
        {
            if (sender != null)
            {
                senderShapes.Add(new KeyValuePair<Component, Shape>(sender, ContactShape(sender)));
            }
        }

        foreach (var receiver in receivers)
        {
            if (receiver == null)
            {
                continue;
            }
            if (!receiverStates.TryGetValue(receiver, out var state))
            {
                state = new ReceiverState();
                receiverStates[receiver] = state;
            }

            var receiverShape = ContactShape(receiver);
            var receiverTags = Field<List<string>>(receiver, "collisionTags") ?? new List<string>();
            var allowSelf = Field(receiver, "allowSelf", true);
            var allowOthers = Field(receiver, "allowOthers", true);
            var faceProximity = receiverShape.Kind == 2 && Field(receiver, "useFaceProximity", false);
            var receiverAvatar = AvatarRoot(receiver.transform);
            var kind = EnumField(receiver, "receiverType");
            state.Senders.Clear();
            state.Hits.Clear();

            foreach (var pair in senderShapes)
            {
                var senderTags = Field<List<string>>(pair.Key, "collisionTags");
                if (senderTags == null)
                {
                    continue;
                }
                var tagBits = 0u;
                for (var i = 0; i < receiverTags.Count && i < 32; i++)
                {
                    if (senderTags.Contains(receiverTags[i]))
                    {
                        tagBits |= 1u << i;
                    }
                }
                if (tagBits == 0u)
                {
                    continue;
                }
                var isSelf = AvatarRoot(pair.Key.transform) == receiverAvatar;
                if ((isSelf && !allowSelf) || (!isSelf && !allowOthers))
                {
                    continue;
                }

                if (!TryContactProximity(pair.Value, receiverShape, faceProximity, out var proximity, out var gap))
                {
                    continue;
                }
                state.Senders.Add(pair.Key.name);
                state.Hits.Add(new ContactHit
                {
                    Sender = pair.Key,
                    Shape = pair.Value,
                    Proximity = proximity,
                    Gap = gap,
                    TagBits = tagBits,
                    SameAvatar = isSelf,
                });
            }

            // Ordem decrescente de proximidade, como no buffer do VRChat.
            state.Hits.Sort((a, b) => b.Proximity != a.Proximity ? b.Proximity.CompareTo(a.Proximity) : a.Gap.CompareTo(b.Gap));
            var colliding = state.Hits.Count > 0;
            if (colliding && !state.Colliding)
            {
                state.EnterTime = now;
            }
            state.Colliding = colliding;
            state.PreviousProximity = state.Proximity;
            state.Proximity = colliding ? state.Hits[0].Proximity : 0f;

            var contactValue = Field(receiver, "paramValue", 1f);
            if (kind == 2)
            {
                state.Value = state.Proximity;
            }
            else if (kind == 1)
            {
                // OnEnter: pulso de um frame no VRChat; aqui fica visível por 0,25 s.
                state.Value = now - state.EnterTime < 0.25 ? contactValue : 0f;
            }
            else
            {
                state.Value = colliding ? contactValue : 0f;
            }

            UpdateAdvancedParameters(receiver, receiverShape, receiverTags, kind, state, deltaTime);
            if (writeMaterialContacts)
            {
                WriteMaterialContacts(receiver, receiverAvatar, state);
            }
            else
            {
                state.ShaderName = null;
            }
        }

        foreach (var pair in senderShapes)
        {
            previousSenderPositions[pair.Key] = pair.Value.Center;
        }
        foreach (var receiver in receivers)
        {
            if (receiver != null)
            {
                previousSenderPositions[receiver] = ContactShape(receiver).Center;
            }
        }
    }

    private static bool TryContactProximity(Shape sender, Shape receiver, bool faceProximity, out float proximity, out float gap)
    {
        proximity = 0f;
        gap = DistanceToCore(sender, receiver, out _);
        if (receiver.Kind != 2)
        {
            if (gap > receiver.Radius)
            {
                return false;
            }
            proximity = receiver.Radius > 1e-5f ? 1f - Mathf.Clamp01(gap / receiver.Radius) : 1f;
            return true;
        }

        if (gap > 0f)
        {
            return false;
        }
        var local = Quaternion.Inverse(receiver.Rotation) * (ClosestSenderPoint(sender, receiver.Center) - receiver.Center);
        var half = Vector3.Max(receiver.HalfSize, Vector3.one * 1e-5f);
        if (faceProximity)
        {
            // Proximidade em relação à face +Z da caixa.
            proximity = Mathf.Clamp01((local.z + half.z) / (2f * half.z));
        }
        else
        {
            var normalized = Mathf.Max(Mathf.Abs(local.x) / half.x, Mathf.Abs(local.y) / half.y, Mathf.Abs(local.z) / half.z);
            proximity = 1f - Mathf.Clamp01(normalized);
        }
        gap = local.magnitude;
        return true;
    }

    private static Vector3 ClosestSenderPoint(Shape sender, Vector3 point)
    {
        if (sender.Kind == 2)
        {
            return ClosestOnBox(point, sender);
        }
        var core = ClosestOnSegment(point, sender.A, sender.B);
        return core + Vector3.ClampMagnitude(point - core, sender.Radius);
    }

    private void UpdateAdvancedParameters(
        Component receiver,
        Shape receiverShape,
        List<string> receiverTags,
        int kind,
        ReceiverState state,
        float deltaTime)
    {
        state.MatchedTags.Clear();
        if (state.Hits.Count == 0)
        {
            state.SenderType = 0;
            state.SenderLocalPosition = Vector3.zero;
            state.SenderLossyScale = Vector3.zero;
            state.SenderVelocity = Vector3.zero;
            state.PointSeparation = -1f;
            state.ProximitySpeed = 0f;
            return;
        }

        var nearest = state.Hits[0];
        var hasDescriptor = avatarDescriptorType != null && nearest.Sender.GetComponentInParent(avatarDescriptorType, true) != null;
        state.SenderType = hasDescriptor ? (nearest.SameAvatar ? 1 : 2) : 3;
        var toLocal = Quaternion.Inverse(receiverShape.Rotation);
        state.SenderLocalPosition = toLocal * (nearest.Shape.Center - receiverShape.Center);
        state.SenderLossyScale = RootOf(nearest.Sender).lossyScale;

        var senderVelocity = previousSenderPositions.TryGetValue(nearest.Sender, out var previousSender)
            ? (nearest.Shape.Center - previousSender) / deltaTime
            : Vector3.zero;
        var receiverVelocity = previousSenderPositions.TryGetValue(receiver, out var previousReceiver)
            ? (receiverShape.Center - previousReceiver) / deltaTime
            : Vector3.zero;
        state.SenderVelocity = toLocal * (senderVelocity - receiverVelocity);
        state.PointSeparation = nearest.Gap < 0.001f ? 0f : nearest.Gap;
        var speed = kind == 2 ? (state.Proximity - state.PreviousProximity) / deltaTime : 0f;
        state.ProximitySpeed = Mathf.Abs(speed) < 0.001f ? 0f : speed;

        for (var i = 0; i < receiverTags.Count && i < 32; i++)
        {
            if ((nearest.TagBits & (1u << i)) != 0u)
            {
                state.MatchedTags.Add(receiverTags[i]);
            }
        }
    }

    // Material Contacts (beta): reproduz _VRChatContact_Transforms_{nome}[9] e _VRChatContact_Data_{nome}[9]
    // de VRCContacts.cginc nos MaterialPropertyBlocks dos renderers definidos no receiver.
    private void WriteMaterialContacts(Component receiver, Transform receiverAvatar, ReceiverState state)
    {
        state.ShaderName = null;
        state.WrittenRenderers = 0;
        if (!Field(receiver, "writeToMaterials", false))
        {
            return;
        }
        var shaderName = Field<string>(receiver, "separateShaderParameter");
        if (string.IsNullOrEmpty(shaderName))
        {
            shaderName = Field<string>(receiver, "parameter");
        }
        if (string.IsNullOrEmpty(shaderName))
        {
            return;
        }
        state.ShaderName = shaderName;

        var writeTags = Field(receiver, "writeToMaterialsContactTags", true);
        var transforms = new Matrix4x4[ShaderBufferSize];
        var data = new Vector4[ShaderBufferSize];
        var count = Mathf.Min(state.Hits.Count, MaxShaderTargets);
        for (var i = 0; i < count; i++)
        {
            var hit = state.Hits[i];
            transforms[i] = ContactShapeMatrix(hit.Sender);
            data[i] = new Vector4(
                BitConverter.Int32BitsToSingle(PlayerId(AvatarRoot(hit.Sender.transform))),
                BitConverter.Int32BitsToSingle(writeTags ? unchecked((int)hit.TagBits) : 0),
                0f,
                0f);
        }
        transforms[MaxShaderTargets] = RootOf(receiver).localToWorldMatrix;
        data[MaxShaderTargets] = new Vector4(
            BitConverter.Int32BitsToSingle(PlayerId(receiverAvatar)),
            BitConverter.Int32BitsToSingle(count),
            0f,
            0f);

        var renderers = Field<Renderer[]>(receiver, "shaderTargetRenderers");
        if (renderers == null)
        {
            return;
        }
        if (propertyBlock == null)
        {
            propertyBlock = new MaterialPropertyBlock();
        }
        var transformsName = "_VRChatContact_Transforms_" + shaderName;
        var dataName = "_VRChatContact_Data_" + shaderName;
        for (var i = 0; i < renderers.Length && i < MaxShaderRenderers; i++)
        {
            var renderer = renderers[i];
            if (renderer == null)
            {
                continue;
            }
            if (!touchedRenderers.TryGetValue(renderer, out var touched))
            {
                touched = new TouchedRenderer { WasEmpty = !renderer.HasPropertyBlock() };
                touchedRenderers[renderer] = touched;
            }
            touched.Names.Add(shaderName);
            renderer.GetPropertyBlock(propertyBlock);
            propertyBlock.SetMatrixArray(transformsName, transforms);
            propertyBlock.SetVectorArray(dataName, data);
            renderer.SetPropertyBlock(propertyBlock);
            state.WrittenRenderers++;
        }
    }

    private static Matrix4x4 ContactShapeMatrix(Component contact)
    {
        var local = Matrix4x4.TRS(
            Field(contact, "position", Vector3.zero),
            Field(contact, "rotation", Quaternion.identity),
            Vector3.one);
        return RootOf(contact).localToWorldMatrix * local;
    }

    private int PlayerId(Transform avatar)
    {
        if (!playerIds.TryGetValue(avatar, out var id))
        {
            id = playerIds.Count + 1;
            playerIds[avatar] = id;
        }
        return id;
    }

    private void ClearMaterialContacts()
    {
        if (touchedRenderers.Count == 0)
        {
            return;
        }
        if (propertyBlock == null)
        {
            propertyBlock = new MaterialPropertyBlock();
        }
        var emptyTransforms = new Matrix4x4[ShaderBufferSize];
        var emptyData = new Vector4[ShaderBufferSize];
        foreach (var pair in touchedRenderers)
        {
            if (pair.Key == null)
            {
                continue;
            }
            if (pair.Value.WasEmpty)
            {
                pair.Key.SetPropertyBlock(null);
                continue;
            }
            pair.Key.GetPropertyBlock(propertyBlock);
            foreach (var name in pair.Value.Names)
            {
                propertyBlock.SetMatrixArray("_VRChatContact_Transforms_" + name, emptyTransforms);
                propertyBlock.SetVectorArray("_VRChatContact_Data_" + name, emptyData);
            }
            pair.Key.SetPropertyBlock(propertyBlock);
        }
        touchedRenderers.Clear();
        SceneView.RepaintAll();
    }

    private static Transform AvatarRoot(Transform transform)
    {
        if (avatarDescriptorType != null)
        {
            var descriptor = transform.GetComponentInParent(avatarDescriptorType, true);
            if (descriptor != null)
            {
                return descriptor.transform;
            }
        }
        return transform.root;
    }

    // ---------------------------------------------------------------- PhysBones

    private void SetSimulation(bool enabled)
    {
        simulatePhysBones = enabled;
        if (enabled)
        {
            RestartSimulation();
        }
        else
        {
            RestoreAllChains();
            chains.Clear();
            grabbedChain = null;
        }
        SceneView.RepaintAll();
    }

    private void RestartSimulation()
    {
        RestoreAllChains();
        chains.Clear();
        grabbedChain = null;
        simulatedBones.Clear();
        simulatedBones.UnionWith(physBones);
        foreach (var bone in physBones)
        {
            var chain = BuildChain(bone);
            if (chain != null)
            {
                chains.Add(chain);
            }
        }
        simulationAccumulator = 0f;
        lastSimulationTime = EditorApplication.timeSinceStartup;
    }

    private void RestoreAllChains()
    {
        foreach (var chain in chains)
        {
            RestoreChain(chain);
        }
    }

    private static void RestoreChain(Chain chain)
    {
        if (!chain.Applied)
        {
            return;
        }
        foreach (var particle in chain.Particles)
        {
            if (!particle.Virtual && particle.Transform != null)
            {
                particle.Transform.localPosition = particle.RestLocalPosition;
                particle.Transform.localRotation = particle.RestLocalRotation;
            }
        }
        chain.Applied = false;
    }

    private static Chain BuildChain(Component bone)
    {
        if (bone == null)
        {
            return null;
        }
        var root = RootOf(bone);
        var ignored = new HashSet<Transform>(Field<List<Transform>>(bone, "ignoreTransforms") ?? new List<Transform>());
        var endpoint = Field(bone, "endpointPosition", Vector3.zero);
        var chain = new Chain { Bone = bone, Root = root };
        var depths = new List<int>();
        AddParticles(chain, depths, root, -1, 0, ignored, endpoint);

        var maxDepth = Mathf.Max(1, depths.Count > 0 ? depths.Max() : 1);
        for (var i = 0; i < chain.Particles.Count; i++)
        {
            var particle = chain.Particles[i];
            particle.Ratio = depths[i] / (float)maxDepth;
            particle.RestPosition = ParticleTransformPosition(chain, particle);
            particle.Position = particle.RestPosition;
            particle.PreviousPosition = particle.RestPosition;
            if (particle.Parent >= 0)
            {
                particle.Length = Vector3.Distance(particle.RestPosition, chain.Particles[particle.Parent].RestPosition);
            }
        }
        chain.LastRootPosition = root.position;
        return chain.Particles.Count > 1 ? chain : null;
    }

    private static void AddParticles(
        Chain chain,
        List<int> depths,
        Transform transform,
        int parent,
        int depth,
        HashSet<Transform> ignored,
        Vector3 endpoint)
    {
        if (chain.Particles.Count >= MaxParticlesPerChain)
        {
            return;
        }
        var index = chain.Particles.Count;
        chain.Particles.Add(new Particle
        {
            Transform = transform,
            Parent = parent,
            RestLocalPosition = transform.localPosition,
            RestLocalRotation = transform.localRotation,
        });
        depths.Add(depth);
        if (parent >= 0)
        {
            chain.Particles[parent].Children.Add(index);
        }

        var childCount = 0;
        foreach (Transform child in transform)
        {
            if (ignored.Contains(child))
            {
                continue;
            }
            childCount++;
            AddParticles(chain, depths, child, index, depth + 1, ignored, endpoint);
        }

        if (childCount == 0 && endpoint != Vector3.zero && chain.Particles.Count < MaxParticlesPerChain)
        {
            var endIndex = chain.Particles.Count;
            chain.Particles.Add(new Particle { Transform = transform, Parent = index, Virtual = true, EndOffset = endpoint });
            depths.Add(depth + 1);
            chain.Particles[index].Children.Add(endIndex);
        }
    }

    private static Vector3 ParticleTransformPosition(Chain chain, Particle particle)
    {
        return particle.Virtual ? particle.Transform.TransformPoint(particle.EndOffset) : particle.Transform.position;
    }

    private void StepChain(Chain chain)
    {
        var bone = chain.Bone;
        if (bone == null || chain.Root == null || chain.Particles.Any(p => p.Transform == null))
        {
            return;
        }

        // Volta à pose de repouso para ler a animação/movimento atual do avatar.
        foreach (var particle in chain.Particles)
        {
            if (!particle.Virtual)
            {
                particle.Transform.localPosition = particle.RestLocalPosition;
                particle.Transform.localRotation = particle.RestLocalRotation;
            }
        }
        foreach (var particle in chain.Particles)
        {
            particle.RestPosition = ParticleTransformPosition(chain, particle);
        }

        var rootDelta = chain.Root.position - chain.LastRootPosition;
        chain.LastRootPosition = chain.Root.position;
        var limitType = EnumField(bone, "limitType");
        var limitRotation = Field(bone, "limitRotation", Vector3.zero);
        var collidersList = Field<IList>(bone, "colliders");
        var colliderShapes = new List<Shape>();
        if (collidersList != null)
        {
            foreach (var item in collidersList)
            {
                if (item is Component collider && collider != null && (!(collider is Behaviour b) || b.isActiveAndEnabled))
                {
                    colliderShapes.Add(ColliderShape(collider));
                }
            }
        }
        var canGrab = EnumField(bone, "allowGrabbing") != 0;
        chain.Grabbed = false;

        var root = chain.Particles[0];
        root.Position = root.RestPosition;
        root.PreviousPosition = root.RestPosition;

        var grabPath = grabbedChain == chain && canGrab && grabbedParticle > 0 && grabbedParticle < chain.Particles.Count
            ? SolveGrabPath(chain, grabbedPosition)
            : null;

        for (var i = 1; i < chain.Particles.Count; i++)
        {
            var particle = chain.Particles[i];
            var parent = chain.Particles[particle.Parent];
            var onGrabPath = grabPath != null && grabPath.Contains(i);
            var ratio = particle.Ratio;
            var pull = Mathf.Clamp01(CurveValue(bone, "pull", "pullCurve", ratio));
            var spring = Mathf.Clamp01(CurveValue(bone, "spring", "springCurve", ratio));
            var stiffness = Mathf.Clamp01(CurveValue(bone, "stiffness", "stiffnessCurve", ratio));
            var gravity = CurveValue(bone, "gravity", "gravityCurve", ratio);
            var falloff = Mathf.Clamp01(CurveValue(bone, "gravityFalloff", "gravityFalloffCurve", ratio));
            var immobile = Mathf.Clamp01(CurveValue(bone, "immobile", "immobileCurve", ratio));
            var radius = CurveValue(bone, "radius", "radiusCurve", ratio) * MaxScale(particle.Transform);

            // Immobile: parte do movimento da raiz é carregada sem inércia.
            if (!onGrabPath)
            {
                particle.Position += rootDelta * immobile;
                particle.PreviousPosition += rootDelta * immobile;
            }

            var restDirection = particle.RestPosition - parent.RestPosition;
            var length = restDirection.magnitude;
            if (length < 1e-6f)
            {
                particle.Position = parent.Position;
                particle.PreviousPosition = parent.Position;
                continue;
            }
            restDirection /= length;
            var hangAmount = Mathf.Clamp(gravity * (1f - falloff * Mathf.Max(0f, Vector3.Dot(restDirection, Vector3.down))), -1f, 1f);
            var goalDirection = hangAmount >= 0f
                ? Vector3.Slerp(restDirection, Vector3.down, hangAmount)
                : Vector3.Slerp(restDirection, Vector3.up, -hangAmount);
            var goal = parent.Position + goalDirection * length;

            if (onGrabPath)
            {
                // Caminho do osso agarrado: a pose vem do SolveGrabPath, sem inércia nem stiffness.
                particle.PreviousPosition = particle.Position;
                ConstrainLength(parent.Position, ref particle.Position, length);
                chain.Grabbed |= i == grabbedParticle;
            }
            else
            {
                var velocity = (particle.Position - particle.PreviousPosition) * Mathf.Lerp(0.2f, 0.97f, spring);
                particle.PreviousPosition = particle.Position;
                particle.Position += velocity;
                particle.Position += (goal - particle.Position) * Mathf.Lerp(0.02f, 0.6f, pull);

                ConstrainLength(parent.Position, ref particle.Position, length);

                var maxDeviation = length * (1f - stiffness) * 2f;
                var deviation = particle.Position - goal;
                if (deviation.magnitude > maxDeviation)
                {
                    particle.Position = goal + deviation.normalized * maxDeviation;
                    ConstrainLength(parent.Position, ref particle.Position, length);
                }
            }

            if (limitType != 0)
            {
                var frame = parent.Virtual ? Quaternion.identity : parent.Transform.rotation;
                var limitFrame = frame * Quaternion.Euler(limitRotation);
                var limitDirection = limitFrame * Quaternion.Inverse(frame) * restDirection;
                var maxX = CurveValue(bone, "maxAngleX", "maxAngleXCurve", ratio);
                var maxZ = CurveValue(bone, "maxAngleZ", "maxAngleZCurve", ratio);
                var direction = ApplyLimit(limitType, particle.Position - parent.Position, limitDirection, limitFrame, maxX, maxZ);
                particle.Position = parent.Position + direction.normalized * length;
            }

            foreach (var shape in colliderShapes)
            {
                PushOutOfCollider(shape, ref particle.Position, radius);
            }
            ConstrainLength(parent.Position, ref particle.Position, length);
        }

        ApplyRotations(chain);
        var first = chain.Particles[0].Children.Count > 0 ? chain.Particles[chain.Particles[0].Children[0]] : null;
        if (first != null)
        {
            var rest = first.RestPosition - chain.Particles[0].RestPosition;
            var simulated = first.Position - chain.Particles[0].Position;
            chain.Angle = Mathf.Clamp01(Vector3.Angle(rest, simulated) / 180f);
        }
    }

    // FABRIK no caminho raiz → osso agarrado: puxa os ossos anteriores com a raiz fixa.
    private HashSet<int> SolveGrabPath(Chain chain, Vector3 target)
    {
        var path = new List<int>();
        for (var i = grabbedParticle; i >= 0; i = chain.Particles[i].Parent)
        {
            path.Add(i);
        }
        path.Reverse();

        var lengths = new float[path.Count];
        for (var i = 1; i < path.Count; i++)
        {
            lengths[i] = Vector3.Distance(chain.Particles[path[i]].RestPosition, chain.Particles[path[i - 1]].RestPosition);
        }

        var last = path.Count - 1;
        for (var iteration = 0; iteration < GrabSolverIterations; iteration++)
        {
            chain.Particles[path[last]].Position = target;
            for (var i = last - 1; i >= 0; i--)
            {
                ConstrainLength(chain.Particles[path[i + 1]].Position, ref chain.Particles[path[i]].Position, lengths[i + 1]);
            }
            chain.Particles[path[0]].Position = chain.Particles[path[0]].RestPosition;
            for (var i = 1; i <= last; i++)
            {
                ConstrainLength(chain.Particles[path[i - 1]].Position, ref chain.Particles[path[i]].Position, lengths[i]);
            }
        }
        return new HashSet<int>(path);
    }

    private static void ConstrainLength(Vector3 parent, ref Vector3 position, float length)
    {
        var offset = position - parent;
        position = offset.sqrMagnitude > 1e-12f ? parent + offset.normalized * length : position;
    }

    private static Vector3 ApplyLimit(int limitType, Vector3 direction, Vector3 limitDirection, Quaternion frame, float maxX, float maxZ)
    {
        if (limitType == 2)
        {
            // Hinge: gira somente em volta do eixo X do frame do limite.
            var normal = frame * Vector3.right;
            direction = Vector3.ProjectOnPlane(direction, normal);
            if (direction.sqrMagnitude < 1e-10f)
            {
                return limitDirection;
            }
        }

        var allowed = maxX;
        if (limitType == 3)
        {
            // Polar: elipse com maxX/maxZ conforme o azimute da direção.
            var planar = Vector3.ProjectOnPlane(direction, limitDirection);
            if (planar.sqrMagnitude > 1e-10f)
            {
                var x = Vector3.Dot(planar.normalized, frame * Vector3.forward);
                var z = Vector3.Dot(planar.normalized, frame * Vector3.right);
                var denominator = Mathf.Sqrt(Mathf.Pow(maxZ * x, 2f) + Mathf.Pow(maxX * z, 2f));
                allowed = denominator > 1e-5f ? maxX * maxZ / denominator : 0f;
            }
        }

        var angle = Vector3.Angle(direction, limitDirection);
        if (angle <= allowed)
        {
            return direction;
        }
        return Vector3.RotateTowards(limitDirection, direction, allowed * Mathf.Deg2Rad, 0f);
    }

    private static void PushOutOfCollider(Shape shape, ref Vector3 position, float radius)
    {
        if (shape.Kind == 3)
        {
            var normal = shape.Rotation * Vector3.up;
            var height = Vector3.Dot(position - shape.Center, normal);
            if (height < radius)
            {
                position += normal * (radius - height);
            }
            return;
        }

        var core = ClosestOnSegment(position, shape.A, shape.B);
        var offset = position - core;
        var distance = offset.magnitude;
        var direction = distance > 1e-6f ? offset / distance : shape.Rotation * Vector3.forward;
        if (shape.Inside)
        {
            var limit = Mathf.Max(0f, shape.Radius - radius);
            if (distance > limit)
            {
                position = core + direction * limit;
            }
        }
        else if (distance < shape.Radius + radius)
        {
            position = core + direction * (shape.Radius + radius);
        }
    }

    private static void ApplyRotations(Chain chain)
    {
        var multiChild = EnumField(chain.Bone, "multiChildType");
        foreach (var particle in chain.Particles)
        {
            if (particle.Virtual || particle.Children.Count == 0)
            {
                continue;
            }

            var origin = particle.Transform.position;
            var restVector = Vector3.zero;
            var simulatedVector = Vector3.zero;
            var count = multiChild == 1 ? 1 : particle.Children.Count;
            for (var i = 0; i < count; i++)
            {
                var child = chain.Particles[particle.Children[i]];
                restVector += ParticleTransformPosition(chain, child) - origin;
                simulatedVector += child.Position - origin;
            }
            if (restVector.sqrMagnitude < 1e-12f || simulatedVector.sqrMagnitude < 1e-12f)
            {
                continue;
            }
            particle.Transform.rotation = Quaternion.FromToRotation(restVector, simulatedVector) * particle.Transform.rotation;
        }
        chain.Applied = true;
    }

    // ---------------------------------------------------------------- Scene View

    private void DrawSceneGui(SceneView sceneView)
    {
        if (EditorApplication.isPlaying || !SdkAvailable)
        {
            return;
        }

        var previousZTest = Handles.zTest;
        Handles.zTest = drawThroughGeometry
            ? UnityEngine.Rendering.CompareFunction.Always
            : UnityEngine.Rendering.CompareFunction.LessEqual;

        var drawGizmos = sceneView.drawGizmos;

        if (showColliders && drawGizmos)
        {
            foreach (var collider in colliders)
            {
                if (collider != null)
                {
                    var shape = ColliderShape(collider);
                    DrawShape(shape, shape.Inside ? new Color(1f, 0.35f, 0.35f) : new Color(1f, 0.85f, 0.2f));
                }
            }
        }

        if (showContacts && drawGizmos)
        {
            foreach (var sender in senders)
            {
                if (sender != null)
                {
                    DrawContact(sender, new Color(1f, 0.55f, 0.15f), null);
                }
            }
            foreach (var receiver in receivers)
            {
                if (receiver != null)
                {
                    receiverStates.TryGetValue(receiver, out var state);
                    var active = liveContacts && state != null && state.Colliding;
                    DrawContact(receiver, active ? new Color(0.3f, 1f, 0.4f) : new Color(0.25f, 0.65f, 1f), state);
                }
            }
        }

        if (showPhysBones)
        {
            if (IsSimulationActive)
            {
                if (drawGizmos)
                {
                    foreach (var chain in chains)
                    {
                        DrawChain(chain, true);
                    }
                }
                HandleGrab();
            }
            else if (drawGizmos)
            {
                foreach (var bone in physBones)
                {
                    var chain = BuildChain(bone);
                    if (chain != null)
                    {
                        DrawChain(chain, false);
                    }
                }
            }
        }

        if (showConstraints && drawGizmos)
        {
            foreach (var constraint in constraints)
            {
                if (constraint != null)
                {
                    DrawConstraint(constraint);
                }
            }
        }

        Handles.zTest = previousZTest;
    }

    private Color WithOpacity(Color color, float factor = 1f)
    {
        color.a = handleOpacity * factor;
        return color;
    }

    private void DrawContact(Component contact, Color color, ReceiverState state)
    {
        var shape = ContactShape(contact);
        DrawShape(shape, color);
        if (!showLabels)
        {
            return;
        }

        var tags = Field<List<string>>(contact, "collisionTags");
        var text = tags != null && tags.Count > 0 ? string.Join(", ", tags) : "(sem tags)";
        if (state != null)
        {
            var parameter = Field<string>(contact, "parameter");
            text = string.Format("{0} = {1:0.00}\n{2}", string.IsNullOrEmpty(parameter) ? contact.name : parameter, state.Value, text);
        }
        Handles.color = WithOpacity(color);
        Handles.Label(shape.Center + Vector3.up * (shape.Radius + 0.01f), text, EditorStyles.miniBoldLabel);
    }

    private void DrawShape(Shape shape, Color color)
    {
        Handles.color = WithOpacity(color);
        switch (shape.Kind)
        {
            case 2:
                using (new Handles.DrawingScope(Handles.color, Matrix4x4.TRS(shape.Center, shape.Rotation, Vector3.one)))
                {
                    Handles.DrawWireCube(Vector3.zero, shape.HalfSize * 2f);
                }
                break;
            case 3:
                var normal = shape.Rotation * Vector3.up;
                Handles.DrawWireDisc(shape.Center, normal, 0.15f);
                Handles.DrawWireDisc(shape.Center, normal, 0.08f);
                Handles.DrawLine(shape.Center, shape.Center + normal * 0.1f);
                break;
            default:
                DrawCapsule(shape.A, shape.B, shape.Radius, shape.Rotation);
                break;
        }
    }

    private static void DrawCapsule(Vector3 a, Vector3 b, float radius, Quaternion rotation)
    {
        var up = rotation * Vector3.up;
        var right = rotation * Vector3.right;
        var forward = rotation * Vector3.forward;
        if ((a - b).sqrMagnitude < 1e-10f)
        {
            Handles.DrawWireDisc(a, up, radius);
            Handles.DrawWireDisc(a, right, radius);
            Handles.DrawWireDisc(a, forward, radius);
            return;
        }

        Handles.DrawWireDisc(a, up, radius);
        Handles.DrawWireDisc(b, up, radius);
        Handles.DrawWireArc(a, right, forward, -180f, radius);
        Handles.DrawWireArc(a, forward, right, 180f, radius);
        Handles.DrawWireArc(b, right, forward, 180f, radius);
        Handles.DrawWireArc(b, forward, right, -180f, radius);
        Handles.DrawLine(a + right * radius, b + right * radius);
        Handles.DrawLine(a - right * radius, b - right * radius);
        Handles.DrawLine(a + forward * radius, b + forward * radius);
        Handles.DrawLine(a - forward * radius, b - forward * radius);
    }

    private void DrawChain(Chain chain, bool simulated)
    {
        var bone = chain.Bone;
        var limitType = EnumField(bone, "limitType");
        var limitRotation = Field(bone, "limitRotation", Vector3.zero);
        foreach (var particle in chain.Particles)
        {
            var position = simulated ? particle.Position : particle.RestPosition;
            var color = Color.Lerp(new Color(0.2f, 1f, 0.9f), new Color(1f, 0.3f, 0.9f), particle.Ratio);
            Handles.color = WithOpacity(color);
            if (particle.Parent >= 0)
            {
                var parent = chain.Particles[particle.Parent];
                var parentPosition = simulated ? parent.Position : parent.RestPosition;
                Handles.DrawAAPolyLine(3f, parentPosition, position);

                if (showPhysBoneLimits && limitType != 0 && !parent.Virtual)
                {
                    var restDirection = particle.RestPosition - parent.RestPosition;
                    var frame = parent.Transform.rotation;
                    var limitFrame = frame * Quaternion.Euler(limitRotation);
                    var direction = limitFrame * Quaternion.Inverse(frame) * restDirection.normalized;
                    var maxX = CurveValue(bone, "maxAngleX", "maxAngleXCurve", particle.Ratio);
                    var maxZ = limitType == 3 ? CurveValue(bone, "maxAngleZ", "maxAngleZCurve", particle.Ratio) : maxX;
                    DrawLimit(limitType, parentPosition, direction, limitFrame, restDirection.magnitude * 0.6f, maxX, maxZ);
                }
            }

            var radius = CurveValue(bone, "radius", "radiusCurve", particle.Ratio) * MaxScale(particle.Transform);
            if (radius > 1e-4f)
            {
                Handles.color = WithOpacity(color, 0.5f);
                var normal = SceneView.currentDrawingSceneView != null
                    ? SceneView.currentDrawingSceneView.camera.transform.forward
                    : Vector3.forward;
                Handles.DrawWireDisc(position, normal, radius);
            }
        }

        if (showLabels && simulated && chain.Grabbed)
        {
            Handles.Label(grabbedPosition, "_IsGrabbed", EditorStyles.miniBoldLabel);
        }
    }

    private void DrawLimit(int limitType, Vector3 origin, Vector3 direction, Quaternion frame, float length, float maxX, float maxZ)
    {
        Handles.color = WithOpacity(new Color(1f, 0.9f, 0.3f), 0.35f);
        if (limitType == 2)
        {
            var normal = frame * Vector3.right;
            var start = Quaternion.AngleAxis(-maxX, normal) * direction;
            Handles.DrawWireArc(origin, normal, start, maxX * 2f, length);
            Handles.DrawLine(origin, origin + start * length);
            Handles.DrawLine(origin, origin + Quaternion.AngleAxis(maxX, normal) * direction * length);
            return;
        }

        var axisA = frame * Vector3.right;
        var axisB = frame * Vector3.forward;
        var previous = Vector3.zero;
        for (var i = 0; i <= 16; i++)
        {
            var azimuth = i / 16f * Mathf.PI * 2f;
            var tilt = Quaternion.AngleAxis(maxX * Mathf.Sin(azimuth), axisA) * Quaternion.AngleAxis(maxZ * Mathf.Cos(azimuth), axisB);
            var point = origin + tilt * direction * length;
            if (i > 0)
            {
                Handles.DrawLine(previous, point);
            }
            if (i % 4 == 0)
            {
                Handles.DrawLine(origin, point);
            }
            previous = point;
        }
    }

    private void HandleGrab()
    {
        if (!allowGrab)
        {
            grabbedChain = null;
            return;
        }

        Chain hitChain = null;
        var hitIndex = -1;
        var hitPosition = Vector3.zero;
        foreach (var chain in chains)
        {
            if (EnumField(chain.Bone, "allowGrabbing") == 0)
            {
                continue;
            }
            for (var i = 1; i < chain.Particles.Count; i++)
            {
                var particle = chain.Particles[i];
                var size = HandleUtility.GetHandleSize(particle.Position) * 0.05f;
                Handles.color = WithOpacity(Color.white, 0.6f);
                var controlId = GUIUtility.GetControlID(FocusType.Passive);
                var moved = Handles.FreeMoveHandle(controlId, particle.Position, size, Vector3.zero, Handles.DotHandleCap);
                if (GUIUtility.hotControl == controlId)
                {
                    hitChain = chain;
                    hitIndex = i;
                    hitPosition = moved;
                }
            }
        }

        if (hitChain != null)
        {
            grabbedChain = hitChain;
            grabbedParticle = hitIndex;
            grabbedPosition = hitPosition;
        }
        else if (GUIUtility.hotControl == 0)
        {
            grabbedChain = null;
            grabbedParticle = -1;
        }
    }

    // ---------------------------------------------------------------- Constraints

    private void DrawConstraint(Component constraint)
    {
        var target = Field<Transform>(constraint, "TargetTransform");
        if (target == null)
        {
            target = constraint.transform;
        }
        var active = Field(constraint, "IsActive", true);
        var sources = ReadSources(constraint);
        var totalWeight = sources.Sum(s => s.Weight);

        foreach (var source in sources)
        {
            var weight = totalWeight > 1e-5f ? source.Weight / totalWeight : 0f;
            Handles.color = WithOpacity(Color.Lerp(new Color(0.5f, 0.5f, 0.5f), new Color(0.75f, 0.45f, 1f), weight));
            Handles.DrawDottedLine(target.position, source.Transform.position, 4f);
            if (showLabels)
            {
                Handles.Label(
                    Vector3.Lerp(target.position, source.Transform.position, 0.5f),
                    string.Format("{0:0.##}", source.Weight),
                    EditorStyles.miniLabel);
            }
        }

        if (!showConstraintGhosts || !active || totalWeight <= 1e-5f)
        {
            return;
        }

        if (!TrySolveConstraint(constraint, target, sources, totalWeight, out var position, out var rotation))
        {
            return;
        }
        var globalWeight = Mathf.Clamp01(Field(constraint, "GlobalWeight", 1f));
        position = Vector3.Lerp(target.position, position, globalWeight);
        rotation = Quaternion.Slerp(target.rotation, rotation, globalWeight);

        var size = HandleUtility.GetHandleSize(position) * 0.4f;
        Handles.color = WithOpacity(new Color(1f, 0.3f, 0.3f));
        Handles.DrawLine(position, position + rotation * Vector3.right * size);
        Handles.color = WithOpacity(new Color(0.3f, 1f, 0.3f));
        Handles.DrawLine(position, position + rotation * Vector3.up * size);
        Handles.color = WithOpacity(new Color(0.3f, 0.5f, 1f));
        Handles.DrawLine(position, position + rotation * Vector3.forward * size);
        Handles.color = WithOpacity(new Color(0.75f, 0.45f, 1f), 0.6f);
        using (new Handles.DrawingScope(Handles.color, Matrix4x4.TRS(position, rotation, Vector3.one)))
        {
            Handles.DrawWireCube(Vector3.zero, Vector3.one * size * 0.3f);
        }
        if ((position - target.position).sqrMagnitude > 1e-8f)
        {
            Handles.DrawDottedLine(target.position, position, 2f);
        }
        if (showLabels)
        {
            Handles.Label(position, constraint.GetType().Name.Replace("VRC", string.Empty), EditorStyles.miniLabel);
        }
    }

    private struct ConstraintSource
    {
        public Transform Transform;
        public float Weight;
        public Vector3 PositionOffset;
        public Vector3 RotationOffset;
    }

    private static List<ConstraintSource> ReadSources(Component constraint)
    {
        var result = new List<ConstraintSource>();
        if (Field<object>(constraint, "Sources") is IEnumerable sources)
        {
            foreach (var source in sources)
            {
                var transform = Field<Transform>(source, "SourceTransform");
                if (transform == null)
                {
                    continue;
                }
                result.Add(new ConstraintSource
                {
                    Transform = transform,
                    Weight = Mathf.Max(0f, Field(source, "Weight", 0f)),
                    PositionOffset = Field(source, "ParentPositionOffset", Vector3.zero),
                    RotationOffset = Field(source, "ParentRotationOffset", Vector3.zero),
                });
            }
        }
        return result;
    }

    private static bool TrySolveConstraint(
        Component constraint,
        Transform target,
        List<ConstraintSource> sources,
        float totalWeight,
        out Vector3 position,
        out Quaternion rotation)
    {
        position = target.position;
        rotation = target.rotation;
        var typeName = constraint.GetType().Name;
        var averagePosition = Vector3.zero;
        var averageRotation = Quaternion.identity;
        var accumulated = 0f;
        foreach (var source in sources)
        {
            var weight = source.Weight / totalWeight;
            var sourcePosition = source.Transform.position;
            var sourceRotation = source.Transform.rotation;
            if (typeName.Contains("Parent"))
            {
                sourcePosition = source.Transform.TransformPoint(source.PositionOffset);
                sourceRotation *= Quaternion.Euler(source.RotationOffset);
            }
            averagePosition += sourcePosition * weight;
            accumulated += weight;
            averageRotation = Quaternion.Slerp(averageRotation, sourceRotation, accumulated > 0f ? weight / accumulated : 0f);
        }

        if (typeName.Contains("Parent"))
        {
            position = averagePosition;
            rotation = averageRotation;
            return true;
        }
        if (typeName.Contains("Position"))
        {
            position = averagePosition + Field(constraint, "PositionOffset", Vector3.zero);
            return true;
        }
        if (typeName.Contains("Rotation"))
        {
            rotation = averageRotation * Quaternion.Euler(Field(constraint, "RotationOffset", Vector3.zero));
            return true;
        }
        if (typeName.Contains("Aim") || typeName.Contains("LookAt"))
        {
            var direction = averagePosition - target.position;
            if (direction.sqrMagnitude < 1e-10f)
            {
                return false;
            }
            var up = Vector3.up;
            var worldUpTransform = Field<Transform>(constraint, "WorldUpTransform");
            if (worldUpTransform != null && (Field(constraint, "UseUpTransform", false) || EnumField(constraint, "WorldUp") != 0))
            {
                up = worldUpTransform.up;
            }
            var aimAxis = typeName.Contains("Aim") ? Field(constraint, "AimAxis", Vector3.forward) : Vector3.forward;
            var upAxis = typeName.Contains("Aim") ? Field(constraint, "UpAxis", Vector3.up) : Vector3.up;
            rotation = Quaternion.LookRotation(direction, up) * Quaternion.Inverse(Quaternion.LookRotation(aimAxis, upAxis));
            if (typeName.Contains("LookAt"))
            {
                rotation *= Quaternion.AngleAxis(Field(constraint, "Roll", 0f), Vector3.forward);
            }
            rotation *= Quaternion.Euler(Field(constraint, "RotationOffset", Vector3.zero));
            return true;
        }
        return false;
    }
}
