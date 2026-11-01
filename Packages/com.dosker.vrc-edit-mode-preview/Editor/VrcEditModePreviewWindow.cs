using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DoskerTools.EditorUi;
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
    private const string PrefsFile = "VrcEditModePreview";
    private const float ImpulseStrength = 0.6f;
    private const string ValidationOk = "VRC_PREVIEW_VALIDATION_OK";
    private const float CalibrationNudge = 0.3f;
    private const float FullTurn = Mathf.PI * 2f;
    private const float BodyNudgeDegrees = 10f;
    private const float PelvisShare = 0.45f;
    private const int SpinePasses = 4;
    private const float MinimumRate = 1f;
    private const float KeepRestTilt = float.NaN;
    private const float FreeArms = float.NaN;
    private const int ArmTableSteps = 40;
    private const float RestFootBlend = 0.06f;
    private const float KinkNudge = 0.2f;
    private const float ArmsDownMuscle = -0.55f;
    private const float SoftLimitStart = 0.8f;
    private const float JumpCycles = 0.7f;
    private const float StandingArms = -70f;
    private static readonly string[] SpineMuscles = { "Spine Front-Back", "Chest Front-Back", "UpperChest Front-Back" };
    private static readonly string[] NeckMuscles = { "Neck Nod Down-Up", "Head Nod Down-Up" };
    private static readonly string[] Fingers = { "Thumb", "Index", "Middle", "Ring", "Little" };
    private static readonly string[] TestMotionLabels =
    {
        "Nenhuma",
        "Movimento/Parado", "Movimento/Andar", "Movimento/Correr", "Movimento/Agachado", "Movimento/Andar agachado", "Movimento/Sentado", "Movimento/Pular",
        "Poses/T-pose", "Poses/A-pose",
        "Pernas/Levantar joelhos", "Pernas/Calcanhar para trás", "Pernas/Levantar perna esticada", "Pernas/Agachamento", "Pernas/Abrir as pernas",
        "Tronco e braços/Erguer os braços", "Tronco e braços/Abraçar o corpo", "Tronco e braços/Torcer o tronco", "Tronco e braços/Inclinar para os lados", "Tronco e braços/Olhar em volta",
    };

    // Layout de Packages/com.vrchat.base/ShaderLibrary/VRCContacts.cginc.
    private const int MaxShaderTargets = 8;
    private const int MaxShaderRenderers = 4;
    private const int ShaderBufferSize = MaxShaderTargets + 1;

    private static readonly Dictionary<(Type, string), FieldInfo> FieldCache = new Dictionary<(Type, string), FieldInfo>();
    private static readonly HashSet<string> MissingFields = new HashSet<string>();
    private static readonly Dictionary<string, int> MuscleIndices = new Dictionary<string, int>();

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

    [Serializable]
    private sealed class Preferences
    {
        public bool showPhysBones = true;
        public bool showPhysBoneLimits = true;
        public bool showColliders = true;
        public bool showContacts = true;
        public bool showConstraints = true;
        public bool showConstraintGhosts = true;
        public bool showLabels = true;
        public bool drawThroughGeometry = true;
        public bool onlySelection;
        public bool allowGrab = true;
        public bool liveContacts = true;
        public bool writeMaterialContacts = true;
        public float handleOpacity = 0.8f;
        public bool followArmatureLinks = true;
        public float testMotionSpeed = 1f;
    }

    private enum TestMotion
    {
        None,
        Idle,
        Walk,
        Run,
        Crouch,
        CrouchWalk,
        Sit,
        Jump,
        TPose,
        APose,
        KneeLift,
        HeelLift,
        LegRaise,
        Squat,
        LegSpread,
        ArmsUp,
        Hug,
        Twist,
        SideBend,
        LookAround
    }

    // Um osso da roupa e o osso do avatar que ele segue, como o VRCFury faz no build.
    private sealed class LinkedBone
    {
        public Transform Prop;
        public Transform Avatar;
        public bool AlignPosition;
        public bool AlignRotation;
        public Vector3 PositionOffset;
        public Quaternion RotationOffset;
        public Vector3 RestLocalPosition;
        public Quaternion RestLocalRotation;
    }

    // Tudo o que define um movimento. As amplitudes são exageradas de propósito: uma animação
    // contida esconde o clipping que ela existe para mostrar.
    private sealed class Gait
    {
        public float Cycles;
        public float Stride;
        public float KneeSwing;
        public float CrouchThigh;
        public float CrouchKnee;
        public float FootRoll;
        public float TorsoTilt = KeepRestTilt;
        public float ArmSwing;
        public float ArmReach;
        public float ArmRaise;
        public float Elbow = 0.15f;
        public float ElbowSwing;
        public float Shoulder;
        public float Twist;
        public float PelvisYaw;
        public float PelvisRoll;
        public float Bob;
        public float Sway;
        public float Lift;
        public float LegLift;
        public float KneeLift;
        public float LegSpread;
        public float Balance;
        public float ArmAngle = FreeArms;
        public float ArmAngleSwing;
        public bool ArmsTogether;
        public float SideBend;
        public float Look;
        public float FingerCurl = 0.35f;
        public float Life = 0.5f;
        public float FootBlend = RestFootBlend;
        public float ShoulderBack;
        public float ShoulderRaise;
        public float ChestArch;
        public float HeadRaise;
    }

    private sealed class SavedBone
    {
        public Transform Transform;
        public Vector3 LocalPosition;
        public Quaternion LocalRotation;
    }

    // O sinal de um músculo muda conforme o rig, então é medido no avatar em vez de assumido.
    private sealed class MotionRig
    {
        public Animator Animator;
        public HumanPoseHandler Handler;
        public HumanPose RestPose;
        public HumanPose Pose;
        public HumanPose Output;
        public float[] RestMuscles;
        public float[] BelowZero;
        public readonly List<Vector2> ArmHeights = new List<Vector2>();
        public Transform Hips;
        public Transform LeftFoot;
        public Transform RightFoot;
        public float RestFootHeight;
        public float LegForward;
        public float LegOutward;
        public float ArmForward;
        public float ToesUp;
        public float ShoulderForward;
        public float TwistForward;
        public float HipHeight;
        public float RestTilt;
        public float RestPelvisTilt;
        public Transform Spine;
        public float BodyRate;
        public float LegRate;
        public Transform Neck;
        public Transform Head;
        public Vector3 RestHeadForward;
        public readonly Dictionary<string, float> MuscleRates = new Dictionary<string, float>();
        public readonly List<SavedBone> Bones = new List<SavedBone>();
    }

    private readonly List<LinkedBone> linkedBones = new List<LinkedBone>();
    private readonly List<MotionRig> motionRigs = new List<MotionRig>();
    private bool followArmatureLinks = true;
    private TestMotion testMotion;
    private float testMotionSpeed = 1f;
    private float testMotionTime;
    private string searchText = "";
    [NonSerialized]
    private UnityEditor.IMGUI.Controls.SearchField searchField;
    private readonly List<string> lintResults = new List<string>();
    private bool lintDone;
    private bool poseOnRelease;
    private int physBoneTransforms;
    private int physBoneColliderLinks;
    private int physBoneCollisionChecks;

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
        public Vector3 LastAvatarPosition;
        public bool Posed;
        public readonly List<Vector3> PoseOffsets = new List<Vector3>();
        public float Stretch;
        public float Squish;
        public bool FollowsLink;
    }

    // Lido uma vez por passo: os valores não mudam entre as partículas de uma cadeia.
    private sealed class BoneCurve
    {
        public float Value;
        public AnimationCurve Curve;

        public BoneCurve(Component bone, string valueName, string curveName)
        {
            Value = Field(bone, valueName, 0f);
            Curve = Field<AnimationCurve>(bone, curveName);
        }

        public float At(float ratio)
        {
            return Curve != null && Curve.length > 0 ? Value * Curve.Evaluate(ratio) : Value;
        }
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

    [MenuItem("Tools/Dosker Tools/Validar Edit-Mode Preview")]
    private static void RunValidationFromMenu()
    {
        RunAutomatedValidation();
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
        wantsMouseMove = true;
        LoadPreferences();
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
        SavePreferences();
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

    private void LoadPreferences()
    {
        var saved = DoskerSettings<Preferences>.Load(PrefsFile);
        showPhysBones = saved.showPhysBones;
        showPhysBoneLimits = saved.showPhysBoneLimits;
        showColliders = saved.showColliders;
        showContacts = saved.showContacts;
        showConstraints = saved.showConstraints;
        showConstraintGhosts = saved.showConstraintGhosts;
        showLabels = saved.showLabels;
        drawThroughGeometry = saved.drawThroughGeometry;
        onlySelection = saved.onlySelection;
        allowGrab = saved.allowGrab;
        liveContacts = saved.liveContacts;
        writeMaterialContacts = saved.writeMaterialContacts;
        handleOpacity = saved.handleOpacity;
        followArmatureLinks = saved.followArmatureLinks;
        testMotionSpeed = saved.testMotionSpeed;
    }

    private void SavePreferences()
    {
        DoskerSettings<Preferences>.Save(PrefsFile, new Preferences
        {
            showPhysBones = showPhysBones,
            showPhysBoneLimits = showPhysBoneLimits,
            showColliders = showColliders,
            showContacts = showContacts,
            showConstraints = showConstraints,
            showConstraintGhosts = showConstraintGhosts,
            showLabels = showLabels,
            drawThroughGeometry = drawThroughGeometry,
            onlySelection = onlySelection,
            allowGrab = allowGrab,
            liveContacts = liveContacts,
            writeMaterialContacts = writeMaterialContacts,
            handleOpacity = handleOpacity,
            followArmatureLinks = followArmatureLinks,
            testMotionSpeed = testMotionSpeed,
        });
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
            ClearPosedState();
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
        searchText = DoskerEditorUi.Search(ref searchField, searchText, GUILayout.ExpandWidth(true));

        scroll = EditorGUILayout.BeginScrollView(scroll);
        DrawHealth();
        EditorGUI.BeginChangeCheck();
        DrawPhysBonesCard();
        DrawCollidersCard();
        DrawContactsCard();
        DrawConstraintsCard();
        DrawLintCard();
        DrawDisplayCard();
        if (EditorGUI.EndChangeCheck())
        {
            componentsDirty = true;
            SceneView.RepaintAll();
        }
        EditorGUILayout.EndScrollView();
        DoskerEditorUi.AboutFooter(typeof(VrcEditModePreviewWindow), "https://github.com/sdosker/dosker-tools/tree/main/Packages/com.dosker.vrc-edit-mode-preview#readme");
    }

    private bool MatchesSearch(string text)
    {
        return string.IsNullOrWhiteSpace(searchText) || text.IndexOf(searchText.Trim(), StringComparison.OrdinalIgnoreCase) >= 0;
    }

    // A reflexão falha em silêncio depois de uma atualização do SDK, então o que faltou fica visível.
    private static void DrawHealth()
    {
        if (MissingFields.Count > 0)
        {
            DoskerEditorUi.Banner(
                "Esta versão do SDK não tem: " + string.Join(", ", MissingFields.OrderBy(name => name)) + ". O que depende disso fica sem efeito.",
                DoskerEditorUi.BannerKind.Warning);
        }
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
                    poseOnRelease = DoskerEditorUi.ChipToggle(
                        poseOnRelease,
                        "Posar",
                        "AvatarPivot",
                        DoskerEditorUi.Purple,
                        "Ao soltar, o osso mantém a pose, se Allow Posing estiver ligado. Clique em Reiniciar para soltar.");
                    if (DoskerEditorUi.IconButton("PreMatCube", "↯", "Sacudir: dá o mesmo impulso a todas as cadeias, para comparar ajustes.", 22f))
                    {
                        ApplyImpulse();
                    }
                }
            }

            EditorGUILayout.Space(4f);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Animação de teste", GUILayout.Width(110f));
                var nextMotion = (TestMotion)EditorGUILayout.Popup((int)testMotion, TestMotionLabels);
                if (nextMotion != testMotion)
                {
                    SetTestMotion(nextMotion);
                }
                using (new EditorGUI.DisabledScope(testMotion == TestMotion.None))
                {
                    testMotionSpeed = EditorGUILayout.Slider(testMotionSpeed, 0f, 2f, GUILayout.MinWidth(110f));
                }
            }
        }
    }

    private void SetTestMotion(TestMotion motion)
    {
        testMotion = motion;
        testMotionTime = 0f;
        if (motion != TestMotion.None && !simulatePhysBones)
        {
            SetSimulation(true);
            return;
        }
        if (simulatePhysBones)
        {
            RestartSimulation();
        }
    }

    private void ApplyImpulse()
    {
        foreach (var chain in chains)
        {
            foreach (var particle in chain.Particles.Skip(1))
            {
                particle.PreviousPosition -= chain.Root.right * (particle.Length * ImpulseStrength);
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
            var nextFollow = EditorGUILayout.ToggleLeft(
                new GUIContent("Seguir Armature Link do VRCFury", "Na simulação, os ossos ligados da roupa acompanham o avatar, como depois do build."),
                followArmatureLinks);
            if (nextFollow != followArmatureLinks)
            {
                followArmatureLinks = nextFollow;
                if (simulatePhysBones)
                {
                    RestartSimulation();
                }
            }
            if (linkedBones.Count > 0)
            {
                EditorGUILayout.LabelField(string.Format("{0} ossos ligados por Armature Link", linkedBones.Count), EditorStyles.miniLabel);
            }
            EditorGUILayout.LabelField(
                string.Format("≈ {0} transforms · {1} colliders · {2} collision checks", physBoneTransforms, physBoneColliderLinks, physBoneCollisionChecks),
                EditorStyles.miniLabel);
            if (physBones.Count > 0 && simulatePhysBones)
            {
                EditorGUILayout.Space(2f);
                foreach (var chain in chains.Where(chain => chain.Bone != null && MatchesSearch(chain.Bone.name)))
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
                foreach (var receiver in receivers.Where(receiver => receiver != null && MatchesSearch(receiver.name)))
                {
                    DrawReceiverRow(receiver);
                }
            }
            DrawTagMatrix();
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

    // Quem aciona quem: por tag, quantos senders existem e quantos receivers escutam.
    private void DrawTagMatrix()
    {
        var sent = CountTags(senders);
        var heard = CountTags(receivers);
        foreach (var tag in sent.Keys.Union(heard.Keys).OrderBy(tag => tag))
        {
            sent.TryGetValue(tag, out var senderCount);
            heard.TryGetValue(tag, out var receiverCount);
            var warning = senderCount == 0 || receiverCount == 0 ? " · sem par na cena" : string.Empty;
            EditorGUILayout.LabelField(string.Format("{0}: {1} senders → {2} receivers{3}", tag, senderCount, receiverCount, warning), EditorStyles.miniLabel);
        }
    }

    private static Dictionary<string, int> CountTags(IEnumerable<Component> contacts)
    {
        var counts = new Dictionary<string, int>();
        foreach (var contact in contacts)
        {
            foreach (var tag in Field<List<string>>(contact, "collisionTags") ?? new List<string>())
            {
                counts.TryGetValue(tag, out var count);
                counts[tag] = count + 1;
            }
        }
        return counts;
    }

    private void DrawLintCard()
    {
        using (var card = new DoskerEditorUi.CardScope(
            "Vrc.Lint",
            "Verificar configuração",
            "console.warnicon.sml",
            DoskerEditorUi.Yellow,
            lintDone ? lintResults.Count.ToString() : null,
            defaultExpanded: false))
        {
            if (!card.Expanded)
            {
                return;
            }
            if (GUILayout.Button("Verificar agora"))
            {
                RunLint();
            }
            if (lintDone && lintResults.Count == 0)
            {
                EditorGUILayout.LabelField("Nenhum problema encontrado.", EditorStyles.miniLabel);
            }
            foreach (var result in lintResults)
            {
                EditorGUILayout.LabelField(result, EditorStyles.wordWrappedMiniLabel);
            }
        }
    }

    private void RunLint()
    {
        lintResults.Clear();
        lintDone = true;

        var claimed = new Dictionary<Transform, Component>();
        var usedColliders = new HashSet<Component>();
        foreach (var bone in physBones)
        {
            var root = RootOf(bone);
            if (root.childCount == 0 && Field(bone, "endpointPosition", Vector3.zero) == Vector3.zero)
            {
                lintResults.Add(string.Format("PhysBone {0}: a raiz não tem filhos nem Endpoint Position, então nada se move.", bone.name));
            }
            if (Field<List<Transform>>(bone, "ignoreTransforms")?.Any(item => item == null) == true)
            {
                lintResults.Add(string.Format("PhysBone {0}: Ignore Transforms tem um item vazio.", bone.name));
            }
            foreach (var transform in root.GetComponentsInChildren<Transform>(true))
            {
                if (claimed.TryGetValue(transform, out var other) && other != bone)
                {
                    lintResults.Add(string.Format("PhysBones {0} e {1} controlam o mesmo osso ({2}).", other.name, bone.name, transform.name));
                    break;
                }
                claimed[transform] = bone;
            }

            var linked = Field<IList>(bone, "colliders");
            if (linked == null)
            {
                continue;
            }
            foreach (var item in linked)
            {
                if (item is Component collider && collider != null)
                {
                    usedColliders.Add(collider);
                }
                else
                {
                    lintResults.Add(string.Format("PhysBone {0}: a lista de colliders tem um item vazio.", bone.name));
                }
            }
        }
        foreach (var collider in colliders.Where(collider => !usedColliders.Contains(collider)))
        {
            lintResults.Add(string.Format("Collider {0}: nenhum PhysBone desta lista o usa.", collider.name));
        }
        LintConstraints();
    }

    private void LintConstraints()
    {
        var dependsOn = new Dictionary<Transform, List<Transform>>();
        foreach (var constraint in constraints)
        {
            var sources = ReadSources(constraint);
            if (sources.Count == 0)
            {
                lintResults.Add(string.Format("Constraint {0}: nenhuma fonte definida.", constraint.name));
            }
            else if (sources.Sum(source => source.Weight) <= 0f)
            {
                lintResults.Add(string.Format("Constraint {0}: todas as fontes têm peso zero.", constraint.name));
            }

            var target = Field<Transform>(constraint, "TargetTransform");
            dependsOn[target != null ? target : constraint.transform] = sources.Select(source => source.Transform).ToList();
        }

        foreach (var start in dependsOn.Keys)
        {
            var seen = new HashSet<Transform>();
            var pending = new Stack<Transform>(dependsOn[start]);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                if (current == start)
                {
                    lintResults.Add(string.Format("Constraints formam um ciclo em {0}.", start.name));
                    break;
                }
                if (seen.Add(current) && dependsOn.TryGetValue(current, out var next))
                {
                    next.ForEach(pending.Push);
                }
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
                "{0}_Angle = {1:0.00} · _Stretch = {2:0.00} · _Squish = {3:0.00}{4}{5}",
                string.IsNullOrEmpty(parameter) ? "(sem parâmetro)" : parameter,
                chain.Angle,
                chain.Stretch,
                chain.Squish,
                chain.Grabbed ? " · _IsGrabbed" : string.Empty,
                chain.Posed ? " · _IsPosed" : string.Empty);
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
                StepSimulation();
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
        CountPhysBoneCost();
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

    private void CountPhysBoneCost()
    {
        physBoneTransforms = 0;
        physBoneColliderLinks = 0;
        physBoneCollisionChecks = 0;
        foreach (var bone in physBones)
        {
            var transforms = RootOf(bone).GetComponentsInChildren<Transform>(true).Length;
            var linked = Field<IList>(bone, "colliders")?.Count ?? 0;
            physBoneTransforms += transforms;
            physBoneColliderLinks += linked;
            physBoneCollisionChecks += transforms * linked;
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
        if (field == null)
        {
            MissingFields.Add(name);
        }
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
            ClearPosedState();
            testMotion = TestMotion.None;
            grabbedChain = null;
        }
        SceneView.RepaintAll();
    }

    // A ordem importa: a roupa segue o avatar já animado, e os PhysBones da própria roupa vêm depois,
    // porque precisam dos ossos pais no lugar.
    private void StepSimulation()
    {
        ApplyTestMotion();
        foreach (var chain in chains.Where(chain => !chain.FollowsLink))
        {
            StepChain(chain);
        }
        ApplyArmatureLinks();
        foreach (var chain in chains.Where(chain => chain.FollowsLink))
        {
            StepChain(chain);
        }
    }

    private void ClearPosedState()
    {
        linkedBones.Clear();
        foreach (var rig in motionRigs)
        {
            rig.Handler.Dispose();
        }
        motionRigs.Clear();
    }

    // ---------------------------------------------------------------- Armature Link

    private static Type FindVrcfuryType()
    {
        return FindType("VF.Model.VRCFury");
    }

    private void BuildArmatureLinks()
    {
        var modelType = FindVrcfuryType();
        if (!followArmatureLinks || modelType == null || avatarDescriptorType == null)
        {
            return;
        }

        var avatars = new List<Component>();
        CollectComponents(avatarDescriptorType, avatars);
        foreach (var avatar in avatars)
        {
            var animator = avatar.GetComponent<Animator>();
            foreach (var component in avatar.GetComponentsInChildren(modelType, true))
            {
                var link = Field<object>(component, "content");
                if (link != null && link.GetType().Name == "ArmatureLink")
                {
                    AddArmatureLink(link, avatar.transform, animator);
                }
            }
        }
    }

    private void AddArmatureLink(object link, Transform avatarRoot, Animator animator)
    {
        var propObject = Field<GameObject>(link, "propBone");
        var avatarBone = propObject != null ? FindLinkTarget(link, avatarRoot, animator) : null;
        if (avatarBone == null)
        {
            return;
        }

        var propBone = propObject.transform;
        var suffix = Field(link, "removeBoneSuffix", string.Empty);
        if (string.IsNullOrWhiteSpace(suffix) && propBone.name.Contains(avatarBone.name) && propBone.name != avatarBone.name)
        {
            suffix = propBone.name.Replace(avatarBone.name, string.Empty);
        }

        var humanoid = new HashSet<Transform>();
        if (animator != null && animator.isHuman)
        {
            for (var bone = HumanBodyBones.Hips; bone < HumanBodyBones.LastBone; bone++)
            {
                var transform = animator.GetBoneTransform(bone);
                if (transform != null)
                {
                    humanoid.Add(transform);
                }
            }
        }

        // PhysBones da própria roupa continuam donos dos seus ossos, a não ser em ossos humanoides.
        var ownedByPhysBones = new HashSet<Transform>();
        foreach (var bone in physBones)
        {
            var root = RootOf(bone);
            if (root.IsChildOf(propBone))
            {
                ownedByPhysBones.UnionWith(root.GetComponentsInChildren<Transform>(true).Where(item => item != root));
            }
        }

        var recursive = Field(link, "recursive", false);
        var pending = new Queue<(Transform prop, Transform avatar)>();
        pending.Enqueue((propBone, avatarBone));
        while (pending.Count > 0)
        {
            var (prop, target) = pending.Dequeue();
            AddLinkedBone(link, prop, target);
            if (!recursive)
            {
                continue;
            }
            foreach (Transform child in prop)
            {
                var searchName = string.IsNullOrEmpty(suffix) ? child.name : child.name.Replace(suffix, string.Empty);
                var match = target.Find(searchName);
                if (match != null && (humanoid.Contains(match) || !ownedByPhysBones.Contains(child)))
                {
                    pending.Enqueue((child, match));
                }
            }
        }
    }

    private static Transform FindLinkTarget(object link, Transform avatarRoot, Animator animator)
    {
        if (!(Field<object>(link, "linkTo") is IEnumerable targets))
        {
            return null;
        }

        foreach (var target in targets)
        {
            Transform found = null;
            if (Field(target, "useBone", false))
            {
                found = animator != null && animator.isHuman ? animator.GetBoneTransform((HumanBodyBones)EnumField(target, "bone")) : null;
            }
            else if (Field(target, "useObj", false))
            {
                var linked = Field<GameObject>(target, "obj");
                found = linked != null ? linked.transform : null;
            }
            else
            {
                found = avatarRoot;
            }

            var offset = Field(target, "offset", string.Empty);
            if (found != null && !string.IsNullOrWhiteSpace(offset))
            {
                found = found.Find(offset);
            }
            if (found != null)
            {
                return found;
            }
        }
        return null;
    }

    private void AddLinkedBone(object link, Transform prop, Transform avatar)
    {
        linkedBones.Add(new LinkedBone
        {
            Prop = prop,
            Avatar = avatar,
            AlignPosition = Field(link, "alignPosition", false),
            AlignRotation = Field(link, "alignRotation", false),
            PositionOffset = avatar.InverseTransformPoint(prop.position),
            RotationOffset = Quaternion.Inverse(avatar.rotation) * prop.rotation,
            RestLocalPosition = prop.localPosition,
            RestLocalRotation = prop.localRotation,
        });
    }

    private void ApplyArmatureLinks()
    {
        foreach (var bone in linkedBones)
        {
            if (bone.Prop == null || bone.Avatar == null)
            {
                continue;
            }
            bone.Prop.position = bone.AlignPosition ? bone.Avatar.position : bone.Avatar.TransformPoint(bone.PositionOffset);
            bone.Prop.rotation = bone.AlignRotation ? bone.Avatar.rotation : bone.Avatar.rotation * bone.RotationOffset;
        }
    }

    private void RestoreArmatureLinks()
    {
        foreach (var bone in Enumerable.Reverse(linkedBones))
        {
            if (bone.Prop != null)
            {
                bone.Prop.localPosition = bone.RestLocalPosition;
                bone.Prop.localRotation = bone.RestLocalRotation;
            }
        }
    }

    // ---------------------------------------------------------------- Animações de teste

    private void BuildMotionRigs()
    {
        if (testMotion == TestMotion.None || avatarDescriptorType == null)
        {
            return;
        }

        var avatars = new List<Component>();
        CollectComponents(avatarDescriptorType, avatars);
        foreach (var avatar in avatars)
        {
            var animator = avatar.GetComponent<Animator>();
            if (animator != null && animator.isHuman && animator.avatar != null && animator.avatar.isValid)
            {
                motionRigs.Add(CreateMotionRig(animator));
            }
        }
    }

    private static MotionRig CreateMotionRig(Animator animator)
    {
        var rig = new MotionRig
        {
            Animator = animator,
            Handler = new HumanPoseHandler(animator.avatar, animator.transform),
            Hips = animator.GetBoneTransform(HumanBodyBones.Hips),
            LeftFoot = animator.GetBoneTransform(HumanBodyBones.LeftFoot),
            RightFoot = animator.GetBoneTransform(HumanBodyBones.RightFoot),
            Neck = animator.GetBoneTransform(HumanBodyBones.Neck),
            Head = animator.GetBoneTransform(HumanBodyBones.Head),
            Spine = animator.GetBoneTransform(HumanBodyBones.Spine),
        };
        if (rig.Neck == null)
        {
            rig.Neck = rig.Head;
        }
        for (var bone = HumanBodyBones.Hips; bone < HumanBodyBones.LastBone; bone++)
        {
            var transform = animator.GetBoneTransform(bone);
            if (transform != null)
            {
                rig.Bones.Add(new SavedBone { Transform = transform, LocalPosition = transform.localPosition, LocalRotation = transform.localRotation });
            }
        }

        rig.Handler.GetHumanPose(ref rig.RestPose);
        rig.Pose = rig.RestPose;
        rig.Pose.muscles = (float[])rig.RestPose.muscles.Clone();
        rig.Output = rig.RestPose;
        rig.Output.muscles = (float[])rig.RestPose.muscles.Clone();
        MeasureKinks(rig);
        rig.RestMuscles = rig.RestPose.muscles.Select((value, index) => value < 0f ? value / rig.BelowZero[index] : value).ToArray();
        rig.HipHeight = rig.Hips != null ? Mathf.Max(0.1f, rig.Hips.position.y - animator.transform.position.y) : 1f;
        rig.RestFootHeight = FootHeight(rig, RestFootBlend);
        var forward = animator.transform.forward;
        rig.LegForward = MeasureSign(rig, "Left Upper Leg Front-Back", HumanBodyBones.LeftLowerLeg, forward);
        rig.LegOutward = MeasureSign(rig, "Left Upper Leg In-Out", HumanBodyBones.LeftLowerLeg, -animator.transform.right);
        rig.ArmForward = MeasureSign(rig, "Left Arm Front-Back", HumanBodyBones.LeftLowerArm, forward);
        rig.ToesUp = MeasureSign(rig, "Left Foot Up-Down", HumanBodyBones.LeftToes, animator.transform.up);
        rig.ShoulderForward = MeasureSign(rig, "Left Shoulder Front-Back", HumanBodyBones.LeftUpperArm, forward);
        rig.TwistForward = MeasureSign(rig, "Chest Twist Left-Right", HumanBodyBones.LeftUpperArm, forward);
        rig.RestHeadForward = rig.Head != null ? rig.Head.forward : forward;
        rig.RestTilt = TorsoTilt(rig);
        rig.RestPelvisTilt = PelvisTilt(rig);
        MeasureSpineRate(rig, "Spine Front-Back", HumanBodyBones.Spine, HumanBodyBones.Chest, HumanBodyBones.UpperChest, HumanBodyBones.Neck);
        MeasureSpineRate(rig, "Chest Front-Back", HumanBodyBones.Chest, HumanBodyBones.UpperChest, HumanBodyBones.Neck, HumanBodyBones.Head);
        MeasureSpineRate(rig, "UpperChest Front-Back", HumanBodyBones.UpperChest, HumanBodyBones.Neck, HumanBodyBones.Head);
        foreach (var muscle in NeckMuscles)
        {
            rig.MuscleRates[muscle] = MeasureRate(rig, muscle, () => HeadPitch(rig));
        }
        MeasureArmHeights(rig);
        rig.LegRate = MeasureRate(rig, "Left Upper Leg Front-Back", () => ThighPitch(rig, HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg));
        rig.BodyRate = MeasureBodyRate(rig);
        return rig;
    }

    // O Unity usa um limite para cada lado do zero, então o osso muda de velocidade de repente quando o
    // músculo cruza o zero. Medir a razão no avatar permite trabalhar numa escala em que o giro é contínuo.
    private static void MeasureKinks(MotionRig rig)
    {
        rig.BelowZero = Enumerable.Repeat(1f, rig.RestPose.muscles.Length).ToArray();
        var pose = rig.RestPose;
        pose.muscles = new float[rig.RestPose.muscles.Length];
        for (var index = 0; index < pose.muscles.Length; index++)
        {
            var boneIndex = HumanTrait.BoneFromMuscle(index);
            var bone = boneIndex >= 0 ? rig.Animator.GetBoneTransform((HumanBodyBones)boneIndex) : null;
            if (bone == null)
            {
                continue;
            }

            rig.Handler.SetHumanPose(ref pose);
            var center = bone.localRotation;
            pose.muscles[index] = KinkNudge;
            rig.Handler.SetHumanPose(ref pose);
            var above = Quaternion.Angle(center, bone.localRotation);
            pose.muscles[index] = -KinkNudge;
            rig.Handler.SetHumanPose(ref pose);
            var below = Quaternion.Angle(center, bone.localRotation);
            pose.muscles[index] = 0f;
            if (above >= 0.01f && below >= 0.01f)
            {
                rig.BelowZero[index] = Mathf.Clamp(above / below, 0.1f, 10f);
            }
        }
        RestoreMotionRig(rig);
    }

    // Tabela de altura do braço por valor de músculo: o ângulo não é uma reta, porque depende de quanto o
    // braço aponta para a frente, e corrigir a cada quadro faz a mão tremer quando o alvo está longe.
    private static void MeasureArmHeights(MotionRig rig)
    {
        var index = MuscleIndex("Left Arm Down-Up");
        var pose = rig.RestPose;
        pose.muscles = (float[])rig.RestPose.muscles.Clone();
        for (var step = 0; step <= ArmTableSteps; step++)
        {
            var muscle = Mathf.Lerp(-1f, 1f, (float)step / ArmTableSteps);
            pose.muscles[index] = muscle;
            rig.Handler.SetHumanPose(ref pose);
            var point = new Vector2(ArmHeight(rig), muscle < 0f ? muscle / rig.BelowZero[index] : muscle);
            // No fim do curso o braço pode descer antes de subir: a tabela começa no ponto mais baixo.
            if (rig.ArmHeights.Count == 1 && point.x <= rig.ArmHeights[0].x)
            {
                rig.ArmHeights[0] = point;
            }
            else if (rig.ArmHeights.Count == 0 || point.x > rig.ArmHeights[rig.ArmHeights.Count - 1].x)
            {
                rig.ArmHeights.Add(point);
            }
        }
        RestoreMotionRig(rig);
    }

    private static float ArmMuscle(MotionRig rig, float angle)
    {
        var table = rig.ArmHeights;
        for (var index = 1; index < table.Count; index++)
        {
            if (angle <= table[index].x)
            {
                return Mathf.Lerp(table[index - 1].y, table[index].y, Mathf.InverseLerp(table[index - 1].x, table[index].x, angle));
            }
        }
        return table.Count > 0 ? table[table.Count - 1].y : 0f;
    }

    private static void ApplyPose(MotionRig rig)
    {
        for (var index = 0; index < rig.Pose.muscles.Length; index++)
        {
            var value = rig.Pose.muscles[index];
            rig.Output.muscles[index] = SoftLimit(value < 0f ? value * rig.BelowZero[index] : value);
        }
        rig.Output.bodyPosition = rig.Pose.bodyPosition;
        rig.Output.bodyRotation = rig.Pose.bodyRotation;
        rig.Handler.SetHumanPose(ref rig.Output);
    }

    private static float MeasureSign(MotionRig rig, string muscle, HumanBodyBones bone, Vector3 direction)
    {
        var transform = rig.Animator.GetBoneTransform(bone);
        var index = MuscleIndex(muscle);
        if (transform == null || index < 0)
        {
            return 1f;
        }

        var before = transform.position;
        var pose = rig.RestPose;
        pose.muscles = (float[])rig.RestPose.muscles.Clone();
        pose.muscles[index] = Mathf.Clamp(pose.muscles[index] + CalibrationNudge, -1f, 1f);
        rig.Handler.SetHumanPose(ref pose);
        var moved = Vector3.Dot(transform.position - before, direction);
        RestoreMotionRig(rig);
        return moved >= 0f ? 1f : -1f;
    }

    // Graus por unidade de músculo, medidos no avatar: o limite de cada osso muda de rig para rig.
    private static float MeasureRate(MotionRig rig, string muscle, Func<float> metric)
    {
        var index = MuscleIndex(muscle);
        if (index < 0)
        {
            return 0f;
        }

        var before = metric();
        var pose = rig.RestPose;
        pose.muscles = (float[])rig.RestPose.muscles.Clone();
        var rest = pose.muscles[index];
        var nudge = rest > 0f ? -CalibrationNudge : CalibrationNudge;
        pose.muscles[index] = rest + nudge;
        rig.Handler.SetHumanPose(ref pose);
        var rate = (metric() - before) / nudge;
        RestoreMotionRig(rig);
        return rest < 0f ? rate * rig.BelowZero[index] : rate;
    }

    // Mede o giro do próprio osso, sem o braço de alavanca até o pescoço, para repartir por igual.
    private static void MeasureSpineRate(MotionRig rig, string muscle, HumanBodyBones bone, params HumanBodyBones[] next)
    {
        var start = rig.Animator.GetBoneTransform(bone);
        var end = next.Select(rig.Animator.GetBoneTransform).FirstOrDefault(item => item != null);
        rig.MuscleRates[muscle] = start != null && end != null
            ? MeasureRate(rig, muscle, () => SideAngle(rig, end.position - start.position))
            : 0f;
    }

    private static float MeasureBodyRate(MotionRig rig)
    {
        var before = TorsoTilt(rig);
        var pose = rig.RestPose;
        pose.muscles = (float[])rig.RestPose.muscles.Clone();
        pose.bodyRotation = Quaternion.AngleAxis(BodyNudgeDegrees, Vector3.right) * pose.bodyRotation;
        rig.Handler.SetHumanPose(ref pose);
        var rate = (TorsoTilt(rig) - before) / BodyNudgeDegrees;
        RestoreMotionRig(rig);
        return rate;
    }

    // Ângulo no plano lateral do avatar: 0 é vertical, e positivo inclina para a frente.
    private static float SideAngle(MotionRig rig, Vector3 direction)
    {
        var root = rig.Animator.transform;
        return Mathf.Atan2(Vector3.Dot(direction, root.forward), Vector3.Dot(direction, root.up)) * Mathf.Rad2Deg;
    }

    private static float TorsoTilt(MotionRig rig)
    {
        return rig.Neck != null && rig.Hips != null ? SideAngle(rig, rig.Neck.position - rig.Hips.position) : 0f;
    }

    private static float PelvisTilt(MotionRig rig)
    {
        return rig.Spine != null && rig.Hips != null ? SideAngle(rig, rig.Spine.position - rig.Hips.position) : 0f;
    }

    private static float HeadPitch(MotionRig rig)
    {
        return rig.Head != null ? Mathf.DeltaAngle(SideAngle(rig, rig.RestHeadForward), SideAngle(rig, rig.Head.forward)) : 0f;
    }

    private static float ThighPitch(MotionRig rig, HumanBodyBones upper, HumanBodyBones lower)
    {
        var thigh = rig.Animator.GetBoneTransform(upper);
        var knee = rig.Animator.GetBoneTransform(lower);
        return thigh != null && knee != null ? -SideAngle(rig, thigh.position - knee.position) : 0f;
    }

    // Altura do braço em graus: 0 é na horizontal, e negativo aponta para baixo.
    private static float ArmHeight(MotionRig rig)
    {
        var shoulder = rig.Animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
        var elbow = rig.Animator.GetBoneTransform(HumanBodyBones.LeftLowerArm);
        if (shoulder == null || elbow == null)
        {
            return 0f;
        }
        var direction = (elbow.position - shoulder.position).normalized;
        return Mathf.Asin(Mathf.Clamp(Vector3.Dot(direction, rig.Animator.transform.up), -1f, 1f)) * Mathf.Rad2Deg;
    }

    private static int MuscleIndex(string name)
    {
        if (MuscleIndices.Count == 0)
        {
            var names = HumanTrait.MuscleName;
            for (var index = 0; index < names.Length; index++)
            {
                MuscleIndices[names[index]] = index;
            }
        }
        return MuscleIndices.TryGetValue(name, out var found) ? found : -1;
    }

    // O mínimo é suave: um mínimo simples muda de direção de repente quando o apoio passa de um pé para
    // o outro, e o corpo inteiro dá um tranco duas vezes por ciclo.
    private static float FootHeight(MotionRig rig, float footBlend)
    {
        var root = rig.Animator.transform.position.y;
        var left = rig.LeftFoot != null ? rig.LeftFoot.position.y : root;
        var right = rig.RightFoot != null ? rig.RightFoot.position.y : root;
        var blend = rig.HipHeight * footBlend;
        var mix = Mathf.Clamp01(0.5f + 0.5f * (right - left) / blend);
        return Mathf.Lerp(right, left, mix) - blend * mix * (1f - mix) - root;
    }

    // Sobe e desce sem quinas, ao contrário de cortar o seno em zero: vai de 0 a 1 uma vez por volta.
    private static float Lobe(float angle)
    {
        var wave = 0.5f + 0.5f * Mathf.Sin(angle);
        return wave * wave;
    }

    private static float Bump(float moment, float start, float end)
    {
        if (moment <= start || moment >= end)
        {
            return 0f;
        }
        var wave = Mathf.Sin((moment - start) / (end - start) * Mathf.PI);
        return wave * wave;
    }

    private void ApplyTestMotion()
    {
        if (testMotion == TestMotion.None)
        {
            return;
        }

        testMotionTime += SimulationStep * testMotionSpeed;
        foreach (var rig in motionRigs)
        {
            ApplyMotionToRig(rig);
        }
    }

    private Gait CurrentGait()
    {
        switch (testMotion)
        {
            case TestMotion.Idle:
                return new Gait { Cycles = 0.3f, ArmSwing = 0.1f, ElbowSwing = 0.1f, Shoulder = 0.15f, Twist = 0.08f, PelvisYaw = 2f, PelvisRoll = 2f, Bob = 0.01f, Sway = 0.012f, Life = 1f };
            case TestMotion.Walk:
                return new Gait
                {
                    Cycles = 0.9f, Stride = 0.42f, KneeSwing = 0.6f, FootRoll = 0.35f, TorsoTilt = 5f, FootBlend = 0.1f,
                    ArmSwing = 0.38f, ArmReach = 0.08f, Elbow = 0.2f, ElbowSwing = 0.35f, Shoulder = 0.25f,
                    Twist = 0.3f, PelvisYaw = 8f, PelvisRoll = 5f, Bob = 0.025f, Sway = 0.018f,
                };
            case TestMotion.Run:
                return new Gait
                {
                    Cycles = 1.5f, Stride = 0.85f, KneeSwing = 1.1f, FootRoll = 0.45f, TorsoTilt = 18f, FootBlend = 0.3f,
                    ArmSwing = 0.6f, ArmReach = 0.12f, Elbow = 0.75f, ElbowSwing = 0.3f, Shoulder = 0.35f,
                    Twist = 0.45f, PelvisYaw = 14f, PelvisRoll = 6f, Bob = 0.05f, Sway = 0.02f,
                };
            case TestMotion.Crouch:
                return new Gait
                {
                    Cycles = 0.35f, CrouchThigh = 1.85f, CrouchKnee = 1.1f, TorsoTilt = 35f,
                    ArmSwing = 0.12f, ArmReach = 0.25f, Elbow = 0.45f, ElbowSwing = 0.15f, Shoulder = 0.2f,
                    Twist = 0.15f, PelvisYaw = 3f, Bob = 0.03f, Sway = 0.02f,
                };
            case TestMotion.CrouchWalk:
                return new Gait
                {
                    Cycles = 0.6f, Stride = 0.7f, KneeSwing = 0.35f, CrouchThigh = 1.33f, CrouchKnee = 1f, FootRoll = 0.3f, TorsoTilt = 52f,
                    ArmSwing = 0.35f, ArmReach = 0.35f, Elbow = 0.55f, ElbowSwing = 0.35f, Shoulder = 0.35f,
                    Twist = 0.4f, PelvisYaw = 10f, PelvisRoll = 5f, Bob = 0.03f, Sway = 0.03f,
                };
            case TestMotion.Sit:
                return new Gait
                {
                    Cycles = 0.25f, CrouchThigh = 2.6f, CrouchKnee = 1.1f, TorsoTilt = 4f,
                    ArmSwing = 0.12f, ArmReach = 0.15f, Elbow = 0.35f, Shoulder = 0.15f, Twist = 0.1f,
                };
            case TestMotion.Jump:
                var moment = Mathf.Repeat(testMotionTime * JumpCycles, 1f);
                var tuck = Bump(moment, 0.02f, 0.4f);
                var air = Bump(moment, 0.28f, 0.72f);
                var land = Bump(moment, 0.6f, 0.98f);
                // Prepara com os braços para trás, sobe jogando-os para cima e amortece ao cair.
                return new Gait
                {
                    Cycles = JumpCycles, CrouchThigh = 1.15f * tuck + 0.25f * air + 0.8f * land, CrouchKnee = 0.9f * tuck + 0.35f * air + 0.7f * land,
                    TorsoTilt = 28f * tuck - 4f * air + 20f * land, ArmReach = 1.1f * air - 0.6f * tuck - 0.2f * land, ArmRaise = 1f * air,
                    Elbow = 0.25f + 0.3f * tuck + 0.25f * land, Lift = 0.45f * air,
                };
            case TestMotion.TPose:
                return StretchGait(0.125f, -4f);
            case TestMotion.APose:
                return StretchGait(0.14f, -42f);
            case TestMotion.KneeLift:
                return new Gait
                {
                    Cycles = 0.55f, LegLift = 1.33f, KneeLift = 1.25f, FootRoll = 0.2f, TorsoTilt = 4f, Balance = 0.05f,
                    ArmSwing = 0.45f, Elbow = 0.6f, ElbowSwing = 0.3f, Shoulder = 0.2f, Twist = 0.2f, PelvisYaw = 5f, PelvisRoll = 4f, Bob = 0.015f,
                };
            case TestMotion.HeelLift:
                return new Gait
                {
                    Cycles = 0.6f, LegLift = -0.15f, KneeLift = 1.5f, FootRoll = 0.3f, TorsoTilt = 6f, Balance = 0.05f,
                    ArmSwing = 0.35f, Elbow = 0.45f, ElbowSwing = 0.3f, Shoulder = 0.2f, Twist = 0.2f, PelvisYaw = 4f, PelvisRoll = 4f, Bob = 0.015f,
                };
            case TestMotion.LegRaise:
                return new Gait
                {
                    Cycles = 0.4f, LegLift = 0.96f, KneeLift = 0.08f, FootRoll = 0.25f, TorsoTilt = 2f, Balance = 0.06f,
                    ArmSwing = 0.3f, ArmReach = 0.1f, Elbow = 0.3f, ElbowSwing = 0.2f, Shoulder = 0.15f, Twist = 0.15f, PelvisYaw = 4f, PelvisRoll = 5f,
                };
            case TestMotion.Squat:
                var depth = 0.5f - 0.5f * Mathf.Cos(testMotionTime * 0.45f * FullTurn);
                return new Gait
                {
                    Cycles = 0.45f, CrouchThigh = 1.6f * depth, CrouchKnee = 1.1f * depth, TorsoTilt = 35f * depth, LegSpread = 0.15f,
                    ArmReach = 0.1f + 0.9f * depth, ArmRaise = 0.2f * depth, Elbow = 0.2f + 0.15f * depth, ArmsTogether = true,
                };
            case TestMotion.LegSpread:
                var open = 0.5f - 0.5f * Mathf.Cos(testMotionTime * 0.4f * FullTurn);
                return new Gait
                {
                    Cycles = 0.4f, LegSpread = 0.15f + 0.8f * open, CrouchThigh = 0.3f * open, CrouchKnee = 0.3f * open, TorsoTilt = 12f * open,
                    ArmAngle = -60f + 20f * open, Elbow = 0.3f, ArmsTogether = true,
                };
            case TestMotion.ArmsUp:
                return new Gait
                {
                    Cycles = 0.35f, ArmAngle = -2f, ArmAngleSwing = 50f, Elbow = 0.12f, ElbowSwing = 0.12f, Shoulder = 0.1f, ArmsTogether = true,
                    Bob = 0.01f,
                };
            case TestMotion.Hug:
                return new Gait
                {
                    Cycles = 0.3f, ArmAngle = -62f, ArmAngleSwing = 5f, ArmSwing = 0.2f, ArmReach = 0.85f, Elbow = 0.5f, ElbowSwing = 0.1f, Shoulder = 0.3f, TorsoTilt = 8f,
                    ArmsTogether = true, Bob = 0.005f, FingerCurl = 0.5f,
                };
            case TestMotion.Twist:
                return new Gait
                {
                    Cycles = 0.35f, Twist = 0.95f, PelvisYaw = 14f, ArmAngle = -35f, ArmAngleSwing = 8f, ArmSwing = 0.4f, Elbow = 0.4f, Shoulder = 0.3f,
                };
            case TestMotion.SideBend:
                return new Gait
                {
                    Cycles = 0.3f, SideBend = 0.75f, PelvisRoll = 3f, Sway = 0.03f, ArmAngle = -55f, ArmAngleSwing = 12f, Elbow = 0.25f, ArmsTogether = true,
                };
            case TestMotion.LookAround:
                return new Gait
                {
                    Cycles = 0.2f, Look = 0.85f, Twist = 0.2f, ArmSwing = 0.08f, Elbow = 0.2f, PelvisYaw = 5f, Sway = 0.012f, Life = 0.6f,
                };
            default:
                return new Gait();
        }
    }

    // Um alongamento em vez de uma pose congelada: em pé, ajeita os ombros, sobe os braços passando um pouco
    // do ponto, fica na pose abrindo o peito e girando o tronco devagar, e volta a ficar em pé.
    private Gait StretchGait(float cycles, float armAngle)
    {
        var moment = Mathf.Repeat(testMotionTime * cycles, 1f);
        var settle = Bump(moment, 0.02f, 0.2f);
        var rise = Bump(moment, 0.18f, 0.38f);
        var hold = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.2f, 0.36f, moment)) * (1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.72f, 0.9f, moment)));
        return new Gait
        {
            Cycles = cycles, ArmAngle = Mathf.Lerp(StandingArms, armAngle, hold) + 6f * Bump(moment, 0.3f, 0.46f), ArmAngleSwing = 3f,
            ArmReach = 0.12f * (1f - hold), ArmSwing = 0.04f, Elbow = 0.04f + 0.2f * (1f - hold) + 0.35f * rise, ElbowSwing = 0.04f,
            ShoulderBack = 0.35f * hold, ShoulderRaise = 0.35f * settle + 0.15f * rise, ChestArch = 13f * hold, HeadRaise = 14f * hold,
            Twist = 0.5f * hold, SideBend = 0.2f * hold, Look = 0.45f * hold, PelvisYaw = 3f, PelvisRoll = 2f, Bob = 0.006f, Sway = 0.016f,
            FingerCurl = 0.4f - 0.33f * hold, Life = 1f,
        };
    }

    private void ApplyMotionToRig(MotionRig rig)
    {
        rig.RestMuscles.CopyTo(rig.Pose.muscles, 0);
        rig.Pose.bodyPosition = rig.RestPose.bodyPosition;
        rig.Pose.bodyRotation = rig.RestPose.bodyRotation;

        var gait = CurrentGait();
        var phase = testMotionTime * gait.Cycles * FullTurn;
        SetLeg(rig, "Left", phase, gait);
        SetLeg(rig, "Right", phase + Mathf.PI, gait);
        SetArm(rig, "Left", gait.ArmsTogether ? phase : phase + Mathf.PI, gait);
        SetArm(rig, "Right", phase, gait);
        AddLife(rig, gait);

        // Os ombros giram contra o quadril: o ombro esquerdo vai à frente com a perna direita.
        var shoulders = Mathf.Sin(phase + Mathf.PI) * gait.Twist * rig.TwistForward;
        AddMuscle(rig, "Spine Twist Left-Right", shoulders * 0.5f);
        AddMuscle(rig, "Chest Twist Left-Right", shoulders);
        AddMuscle(rig, "UpperChest Twist Left-Right", shoulders);
        AddMuscle(rig, "Neck Turn Left-Right", -shoulders * 0.5f);

        foreach (var (muscle, share) in new[] { ("Spine Left-Right", 0.6f), ("Chest Left-Right", 1f), ("UpperChest Left-Right", 1f) })
        {
            AddMuscle(rig, muscle, Mathf.Sin(phase) * gait.SideBend * share);
        }
        AddMuscle(rig, "Neck Turn Left-Right", Mathf.Sin(phase) * gait.Look * 0.8f);
        AddMuscle(rig, "Head Turn Left-Right", Mathf.Sin(phase - 0.5f) * gait.Look);
        AddMuscle(rig, "Head Nod Down-Up", Mathf.Sin(phase * 2f + 0.7f) * gait.Look * 0.35f);
        AddDegrees(rig, "Chest Front-Back", -gait.ChestArch * 0.5f);
        AddDegrees(rig, "UpperChest Front-Back", -gait.ChestArch * 0.5f);
        AddDegrees(rig, "Head Nod Down-Up", -gait.HeadRaise);

        var pelvis = Quaternion.AngleAxis(Mathf.Sin(phase) * gait.PelvisYaw, Vector3.up)
                     * Quaternion.AngleAxis(Mathf.Cos(phase) * gait.PelvisRoll, Vector3.forward);
        rig.Pose.bodyRotation = pelvis * rig.Pose.bodyRotation;

        ApplyPose(rig);
        if (!float.IsNaN(gait.TorsoTilt))
        {
            LeanTorso(rig, gait.TorsoTilt);
        }

        // Mantém o pé mais baixo no chão, para o agachado descer em vez de levantar os pés. O peso
        // passa para o pé de apoio, e o corpo sobe no meio de cada passo.
        if (rig.Hips != null)
        {
            var root = rig.Animator.transform;
            var bob = gait.Bob * rig.HipHeight * (0.5f + 0.5f * Mathf.Cos(phase * 2f));
            var height = rig.RestFootHeight - FootHeight(rig, gait.FootBlend) + gait.Lift + bob;
            var sideways = Mathf.Cos(phase) * gait.Sway + Mathf.Sin(phase) * gait.Balance + Mathf.Sin(testMotionTime * 0.61f) * 0.006f * gait.Life;
            rig.Hips.position += root.up * height + root.right * (sideways * rig.HipHeight);
        }
    }

    // A coluna é um arco: a pelve inclina e os ossos acima repartem o resto por igual. As duas correções
    // se alternam porque o bodyRotation é uma média do quadril com a coluna: dobrar a coluna para a frente
    // gira o quadril para trás, e só medindo a própria pelve dá para evitar a quebra no meio das costas.
    private static void LeanTorso(MotionRig rig, float target)
    {
        var leftThigh = ThighPitch(rig, HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg);
        var rightThigh = ThighPitch(rig, HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg);
        var pelvisTarget = rig.RestPelvisTilt + (target - rig.RestTilt) * PelvisShare;

        var bendable = SpineMuscles.Where(muscle => Mathf.Abs(rig.MuscleRates[muscle]) >= MinimumRate).ToList();
        for (var pass = 0; pass < SpinePasses; pass++)
        {
            if (Mathf.Abs(rig.BodyRate) >= 0.1f)
            {
                var pelvis = Mathf.DeltaAngle(PelvisTilt(rig), pelvisTarget) / rig.BodyRate;
                rig.Pose.bodyRotation = Quaternion.AngleAxis(pelvis, Vector3.right) * rig.Pose.bodyRotation;
                ApplyPose(rig);
            }

            var missing = target - TorsoTilt(rig);
            foreach (var muscle in bendable)
            {
                AddMuscle(rig, muscle, missing / bendable.Count / rig.MuscleRates[muscle]);
            }
            ApplyPose(rig);
        }

        if (Mathf.Abs(rig.LegRate) >= MinimumRate)
        {
            AddMuscle(rig, "Left Upper Leg Front-Back", (leftThigh - ThighPitch(rig, HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg)) / rig.LegRate);
            AddMuscle(rig, "Right Upper Leg Front-Back", (rightThigh - ThighPitch(rig, HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg)) / rig.LegRate);
            ApplyPose(rig);
        }

        // A cabeça continua olhando para a frente, em vez de seguir o tronco para o chão.
        var pitch = HeadPitch(rig);
        var neck = NeckMuscles.Where(muscle => Mathf.Abs(rig.MuscleRates[muscle]) >= MinimumRate).ToList();
        foreach (var muscle in neck)
        {
            AddMuscle(rig, muscle, -pitch / neck.Count / rig.MuscleRates[muscle]);
        }
        ApplyPose(rig);
    }

    private static void SetLeg(MotionRig rig, string side, float phase, Gait gait)
    {
        var lift = Lobe(phase);
        AddMuscle(rig, side + " Upper Leg Front-Back", (Mathf.Sin(phase) * gait.Stride + gait.CrouchThigh + lift * gait.LegLift) * rig.LegForward);
        AddMuscle(rig, side + " Upper Leg In-Out", gait.LegSpread * rig.LegOutward);
        // O joelho dobra enquanto a perna volta para a frente, e nunca passa de esticado.
        AddMuscle(rig, side + " Lower Leg Stretch", -(Lobe(phase + 1.2f) * gait.KneeSwing + gait.CrouchKnee + lift * gait.KneeLift));
        // A canela inclinada leva o pé junto. Sem isso, ele fica na ponta e atravessa o chão.
        var flat = Mathf.Clamp(gait.CrouchKnee - gait.CrouchThigh * 0.5f, 0f, 1f) * 0.6f;
        AddMuscle(rig, side + " Foot Up-Down", (flat + Mathf.Sin(phase + 0.6f) * gait.FootRoll) * rig.ToesUp);
    }

    private static void SetArm(MotionRig rig, string side, float phase, Gait gait)
    {
        var swing = Mathf.Sin(phase);
        var height = side + " Arm Down-Up";
        var armsDown = ArmsDownMuscle / rig.BelowZero[MuscleIndex(height)] + gait.ArmRaise;
        SetMuscle(rig, height, float.IsNaN(gait.ArmAngle) ? armsDown + swing * swing * gait.ArmSwing * 0.1f : ArmMuscle(rig, gait.ArmAngle + swing * gait.ArmAngleSwing));
        AddMuscle(rig, side + " Arm Front-Back", (swing * gait.ArmSwing + gait.ArmReach) * rig.ArmForward);
        AddMuscle(rig, side + " Shoulder Front-Back", (swing * gait.Shoulder - gait.ShoulderBack) * rig.ShoulderForward);
        AddMuscle(rig, side + " Shoulder Down-Up", gait.ShoulderRaise);
        // O antebraço e a mão chegam atrasados, e o cotovelo fecha mais na ida para a frente.
        SetMuscle(rig, side + " Forearm Stretch", 1f - gait.Elbow - Lobe(phase - 0.4f) * gait.ElbowSwing);
        AddMuscle(rig, side + " Hand Down-Up", Mathf.Sin(phase - 0.8f) * gait.ArmSwing * 0.4f);
        for (var finger = 0; finger < Fingers.Length; finger++)
        {
            // O mindinho fecha mais que o indicador, como numa mão solta.
            var curl = gait.FingerCurl * (0.7f + finger * 0.15f) + Mathf.Sin(phase + finger) * 0.04f;
            for (var joint = 1; joint <= 3; joint++)
            {
                AddMuscle(rig, $"{side} {Fingers[finger]} {joint} Stretched", -curl);
            }
        }
    }

    // Ondas lentas com períodos que não se alinham, nem entre si nem com o ciclo: o corpo respira e olha
    // em volta, e a animação nunca se repete inteira, então não há um ponto em que ela pareça recomeçar.
    private void AddLife(MotionRig rig, Gait gait)
    {
        var breath = Mathf.Sin(testMotionTime * 1.7f) * gait.Life;
        AddMuscle(rig, "Left Shoulder Down-Up", breath * 0.05f);
        AddMuscle(rig, "Right Shoulder Down-Up", breath * 0.05f);
        AddMuscle(rig, "UpperChest Front-Back", breath * 0.04f);
        AddMuscle(rig, "Head Turn Left-Right", (Mathf.Sin(testMotionTime * 0.43f) + 0.5f * Mathf.Sin(testMotionTime * 1.03f)) * 0.12f * gait.Life);
        AddMuscle(rig, "Head Nod Down-Up", Mathf.Sin(testMotionTime * 0.77f) * 0.06f * gait.Life);
        AddMuscle(rig, "Head Tilt Left-Right", Mathf.Sin(testMotionTime * 0.53f) * 0.06f * gait.Life);
        rig.Pose.bodyRotation = Quaternion.AngleAxis(Mathf.Sin(testMotionTime * 0.61f) * 1.2f * gait.Life, Vector3.forward) * rig.Pose.bodyRotation;
    }

    private static void AddDegrees(MotionRig rig, string name, float degrees)
    {
        if (rig.MuscleRates.TryGetValue(name, out var rate) && Mathf.Abs(rate) >= MinimumRate)
        {
            AddMuscle(rig, name, degrees / rate);
        }
    }

    private static void AddMuscle(MotionRig rig, string name, float amount)
    {
        var index = MuscleIndex(name);
        if (index >= 0)
        {
            rig.Pose.muscles[index] += amount;
        }
    }

    // Achata aos poucos perto do limite. Um corte seco para o osso de repente no meio do ciclo.
    private static float SoftLimit(float value)
    {
        var excess = Mathf.Abs(value) - SoftLimitStart;
        if (excess <= 0f)
        {
            return value;
        }
        var room = 1f - SoftLimitStart;
        return Mathf.Sign(value) * (SoftLimitStart + room * (1f - Mathf.Exp(-excess / room)));
    }

    private static void SetMuscle(MotionRig rig, string name, float value)
    {
        var index = MuscleIndex(name);
        if (index >= 0)
        {
            rig.Pose.muscles[index] = value;
        }
    }

    private static void RestoreMotionRig(MotionRig rig)
    {
        foreach (var bone in rig.Bones)
        {
            if (bone.Transform != null)
            {
                bone.Transform.localPosition = bone.LocalPosition;
                bone.Transform.localRotation = bone.LocalRotation;
            }
        }
    }

    private void RestartSimulation()
    {
        RestoreAllChains();
        chains.Clear();
        ClearPosedState();
        grabbedChain = null;
        simulatedBones.Clear();
        simulatedBones.UnionWith(physBones);
        BuildMotionRigs();
        BuildArmatureLinks();
        var linkedProps = new HashSet<Transform>(linkedBones.Select(bone => bone.Prop));
        foreach (var bone in physBones)
        {
            var chain = BuildChain(bone);
            if (chain != null)
            {
                for (var parent = chain.Root; parent != null && !chain.FollowsLink; parent = parent.parent)
                {
                    chain.FollowsLink = linkedProps.Contains(parent);
                }
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
        RestoreArmatureLinks();
        foreach (var rig in motionRigs)
        {
            RestoreMotionRig(rig);
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
        var avatar = AvatarRoot(root);
        chain.LastAvatarPosition = avatar != null ? avatar.position : root.position;
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
        if (chain.Posed)
        {
            HoldPose(chain);
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
        // World: o Immobile só desconta o deslocamento do avatar, não o movimento do corpo.
        var avatarTransform = AvatarRoot(chain.Root);
        var avatarPosition = avatarTransform != null ? avatarTransform.position : chain.Root.position;
        var avatarDelta = avatarPosition - chain.LastAvatarPosition;
        chain.LastAvatarPosition = avatarPosition;
        var immobileDelta = EnumField(bone, "immobileType") == 1 ? avatarDelta : rootDelta;
        var usesStiffness = EnumField(bone, "integrationType") == 1;
        var pullCurve = new BoneCurve(bone, "pull", "pullCurve");
        var springCurve = new BoneCurve(bone, "spring", "springCurve");
        var stiffnessCurve = new BoneCurve(bone, "stiffness", "stiffnessCurve");
        var gravityCurve = new BoneCurve(bone, "gravity", "gravityCurve");
        var falloffCurve = new BoneCurve(bone, "gravityFalloff", "gravityFalloffCurve");
        var immobileCurve = new BoneCurve(bone, "immobile", "immobileCurve");
        var radiusCurve = new BoneCurve(bone, "radius", "radiusCurve");
        var maxAngleXCurve = new BoneCurve(bone, "maxAngleX", "maxAngleXCurve");
        var maxAngleZCurve = new BoneCurve(bone, "maxAngleZ", "maxAngleZCurve");
        var maxStretch = Mathf.Max(0f, Field(bone, "maxStretch", 0f));
        var maxSquish = Mathf.Clamp01(Field(bone, "maxSquish", 0f));
        var stretchMotion = Mathf.Clamp01(Field(bone, "stretchMotion", 0f));
        var strongestStretch = 0f;
        var strongestSquish = 0f;
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
            var pull = Mathf.Clamp01(pullCurve.At(ratio));
            var spring = Mathf.Clamp01(springCurve.At(ratio));
            var stiffness = usesStiffness ? Mathf.Clamp01(stiffnessCurve.At(ratio)) : 0f;
            var gravity = gravityCurve.At(ratio);
            var falloff = Mathf.Clamp01(falloffCurve.At(ratio));
            var immobile = Mathf.Clamp01(immobileCurve.At(ratio));
            var radius = radiusCurve.At(ratio) * MaxScale(particle.Transform);

            // Immobile: parte do movimento da raiz é carregada sem inércia.
            if (!onGrabPath)
            {
                particle.Position += immobileDelta * immobile;
                particle.PreviousPosition += immobileDelta * immobile;
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
            // Só o arraste estica por inteiro; em movimento livre, vale a fração de Stretch Motion.
            var longest = length * (1f + maxStretch * (onGrabPath ? 1f : stretchMotion));
            var shortest = length * (1f - maxSquish);

            if (onGrabPath)
            {
                // Caminho do osso agarrado: a pose vem do SolveGrabPath, sem inércia nem stiffness.
                particle.PreviousPosition = particle.Position;
                ConstrainLength(parent.Position, ref particle.Position, shortest, longest);
                chain.Grabbed |= i == grabbedParticle;
            }
            else
            {
                var velocity = (particle.Position - particle.PreviousPosition) * Mathf.Lerp(0.2f, 0.97f, spring);
                particle.PreviousPosition = particle.Position;
                particle.Position += velocity;
                particle.Position += (goal - particle.Position) * Mathf.Lerp(0.02f, 0.6f, pull);

                ConstrainLength(parent.Position, ref particle.Position, shortest, longest);

                if (usesStiffness)
                {
                    var maxDeviation = length * (1f - stiffness) * 2f;
                    var deviation = particle.Position - goal;
                    if (deviation.magnitude > maxDeviation)
                    {
                        particle.Position = goal + deviation.normalized * maxDeviation;
                        ConstrainLength(parent.Position, ref particle.Position, shortest, longest);
                    }
                }
            }

            if (limitType != 0)
            {
                var frame = parent.Virtual ? Quaternion.identity : parent.Transform.rotation;
                var limitFrame = frame * Quaternion.Euler(limitRotation);
                var limitDirection = limitFrame * Quaternion.Inverse(frame) * restDirection;
                var maxX = maxAngleXCurve.At(ratio);
                var maxZ = maxAngleZCurve.At(ratio);
                var limbLength = Vector3.Distance(particle.Position, parent.Position);
                var direction = ApplyLimit(limitType, particle.Position - parent.Position, limitDirection, limitFrame, maxX, maxZ);
                particle.Position = parent.Position + direction.normalized * limbLength;
            }

            foreach (var shape in colliderShapes)
            {
                PushOutOfCollider(shape, ref particle.Position, radius);
            }
            ConstrainLength(parent.Position, ref particle.Position, shortest, longest);

            var currentLength = Vector3.Distance(particle.Position, parent.Position);
            if (maxStretch > 0f)
            {
                strongestStretch = Mathf.Max(strongestStretch, Mathf.Clamp01((currentLength / length - 1f) / maxStretch));
            }
            if (maxSquish > 0f)
            {
                strongestSquish = Mathf.Max(strongestSquish, Mathf.Clamp01((1f - currentLength / length) / maxSquish));
            }
        }

        chain.Stretch = strongestStretch;
        chain.Squish = strongestSquish;
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
        ConstrainLength(parent, ref position, length, length);
    }

    private static void ConstrainLength(Vector3 parent, ref Vector3 position, float shortest, float longest)
    {
        var offset = position - parent;
        var distance = offset.magnitude;
        position = distance > 1e-6f ? parent + offset / distance * Mathf.Clamp(distance, shortest, longest) : position;
    }

    private void StartPose(Chain chain)
    {
        if (EnumField(chain.Bone, "allowPosing") == 0)
        {
            return;
        }
        chain.PoseOffsets.Clear();
        chain.PoseOffsets.AddRange(chain.Particles.Select(particle => chain.Root.InverseTransformPoint(particle.Position)));
        chain.Posed = true;
    }

    // A pose fica presa à raiz, como um osso posado acompanha quem o carrega.
    private static void HoldPose(Chain chain)
    {
        foreach (var particle in chain.Particles.Where(particle => !particle.Virtual))
        {
            particle.Transform.localPosition = particle.RestLocalPosition;
            particle.Transform.localRotation = particle.RestLocalRotation;
        }
        for (var i = 0; i < chain.Particles.Count; i++)
        {
            chain.Particles[i].Position = chain.Root.TransformPoint(chain.PoseOffsets[i]);
        }
        ApplyRotations(chain);
    }

    // Sem o SDK ou o Unity gráfico não há como ver o arraste, mas a matemática e a restauração podem ser conferidas.
    public static void RunAutomatedValidation()
    {
        var failures = new List<string>();
        void Check(string name, bool passed)
        {
            if (!passed)
            {
                failures.Add(name);
            }
        }

        Vector3 CheckLength(float distance, float shortest, float longest)
        {
            var position = new Vector3(distance, 0f, 0f);
            ConstrainLength(Vector3.zero, ref position, shortest, longest);
            return position;
        }
        Check("length keeps values inside the range", Mathf.Approximately(CheckLength(1f, 0.5f, 2f).x, 1f));
        Check("length clamps to the longest", Mathf.Approximately(CheckLength(3f, 0.5f, 2f).x, 2f));
        Check("length clamps to the shortest", Mathf.Approximately(CheckLength(0.1f, 0.5f, 2f).x, 0.5f));

        var limited = ApplyLimit(1, new Vector3(1f, 0f, 1f), Vector3.forward, Quaternion.identity, 10f, 10f);
        Check("angle limit", Mathf.Abs(Vector3.Angle(limited, Vector3.forward) - 10f) < 0.1f);

        var outside = new Vector3(0.5f, 0f, 0f);
        PushOutOfCollider(new Shape { Kind = 0, Center = Vector3.zero, Rotation = Quaternion.identity, Radius = 1f }, ref outside, 0.1f);
        Check("collider push", Mathf.Abs(outside.magnitude - 1.1f) < 0.001f);

        var curve = new BoneCurve(null, "pull", "pullCurve") { Value = 2f, Curve = AnimationCurve.Linear(0f, 0f, 1f, 1f) };
        Check("curves multiply the value", Mathf.Approximately(curve.At(0.5f), 1f));

        foreach (var muscle in SpineMuscles.Concat(NeckMuscles).Concat(new[] { "Left Shoulder Front-Back", "Left Hand Down-Up", "Chest Twist Left-Right", "UpperChest Twist Left-Right", "Spine Twist Left-Right", "Neck Turn Left-Right", "Left Upper Leg Front-Back", "Right Lower Leg Stretch", "Left Foot Up-Down", "Left Arm Down-Up", "Left Arm Front-Back", "Right Forearm Stretch" }))
        {
            Check("Muscle " + muscle, MuscleIndex(muscle) >= 0);
        }

        ResolveTypes();
        Check("SDK types resolve", physBoneType != null && contactType != null && constraintType != null);
        foreach (var field in new[] { "immobileType", "integrationType", "maxStretch", "maxSquish", "stretchMotion", "allowPosing", "multiChildType" })
        {
            Check("PhysBone field " + field, physBoneType == null || GetField(physBoneType, field) != null);
        }

        if (failures.Count > 0)
        {
            Debug.LogError("VRC preview validation failed: " + string.Join("; ", failures));
            return;
        }
        Debug.Log(ValidationOk);
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
                    var maxX = new BoneCurve(bone, "maxAngleX", "maxAngleXCurve").At(particle.Ratio);
                    var maxZ = limitType == 3 ? new BoneCurve(bone, "maxAngleZ", "maxAngleZCurve").At(particle.Ratio) : maxX;
                    DrawLimit(limitType, parentPosition, direction, limitFrame, restDirection.magnitude * 0.6f, maxX, maxZ);
                }
            }

            var radius = new BoneCurve(bone, "radius", "radiusCurve").At(particle.Ratio) * MaxScale(particle.Transform);
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
            hitChain.Posed = false;
            grabbedChain = hitChain;
            grabbedParticle = hitIndex;
            grabbedPosition = hitPosition;
        }
        else if (GUIUtility.hotControl == 0)
        {
            if (grabbedChain != null && poseOnRelease)
            {
                StartPose(grabbedChain);
            }
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
