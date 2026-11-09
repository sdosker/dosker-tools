using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using DoskerTools.EditorUi;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using static DoskerTools.EditorUi.DoskerLocale;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

public sealed partial class ShaderViewVrDebuggerWindow : EditorWindow
{
    private const int FirstUserLayer = 8;
    private const string SceneAsset = "Assets/ShaderView/ShaderViewPreview.unity";
    private const string MaterialAsset = "Assets/ShaderView/Materials/Center 1.mat";
    // O caminho do shader muda entre projetos; o GUID do .meta, não.
    private const string ShaderGuid = "0510857c96ead304b956c1a90edfcfe8";
    private const string TestObjectName = "Shader under test / ScreenSpace Ubershader";
    private static readonly string[] VrAspectLabels = { "1:1", "4:3", "16:9", "21:9" };
    private static readonly float[] VrAspectRatios = { 1f, 4f / 3f, 16f / 9f, 21f / 9f };
    // Listas com texto são propriedades para seguir o idioma escolhido.
    private static string[] TabLabels => new[] { "Debugger", Tr("Estéreo", "Stereo") };
    private const string SettingsFile = "ShaderView";
    private static string[] AmbientLabels => new[] { Tr("Da cena", "From the scene"), Tr("Sem luz ambiente", "No ambient light"), Tr("Noite", "Night"), Tr("Dia", "Day") };
    private static readonly Color[] AmbientColors = { Color.clear, Color.black, new Color(0.05f, 0.06f, 0.1f), new Color(0.75f, 0.78f, 0.82f) };
    private static readonly Regex VrChatGlobal = new Regex(@"_VRChat\w+", RegexOptions.Compiled);

    // Valores aproximados por faixa de FOV. Não representam a óptica de um modelo específico.
    private static (string label, float fov, float ipd, float cant, float asymmetry)[] StereoPresets => new[]
    {
        (Tr("Estreito (≈90°)", "Narrow (≈90°)"), 90f, 0.063f, 0f, 0.04f),
        (Tr("Comum (≈105°)", "Typical (≈105°)"), 105f, 0.064f, 0f, 0.06f),
        (Tr("Amplo (≈125°)", "Wide (≈125°)"), 125f, 0.064f, 5f, 0.08f),
        (Tr("Ultra-amplo inclinado (≈150°)", "Ultra-wide canted (≈150°)"), 150f, 0.064f, 10f, 0.12f),
    };

    // Globais que o cliente do VRChat define: ver creators.vrchat.com, "VRChat Shader Globals".
    private static readonly int MirrorModeId = Shader.PropertyToID("_VRChatMirrorMode");
    private static readonly int MirrorCameraPosId = Shader.PropertyToID("_VRChatMirrorCameraPos");
    private static readonly int FaceMirrorModeId = Shader.PropertyToID("_VRChatFaceMirrorMode");
    private static readonly int CameraModeId = Shader.PropertyToID("_VRChatCameraMode");
    private static readonly int CameraMaskId = Shader.PropertyToID("_VRChatCameraMask");

    // Globais só do ShaderView: simulam a visão principal em VR. O VRChat nunca as define.
    private const string SimulatedVrVariable = "_ShaderView_SimulatedVR";
    private static readonly int SimulatedVrId = Shader.PropertyToID(SimulatedVrVariable);
    private static readonly int SimulatedEyeIndexId = Shader.PropertyToID("_ShaderView_SimulatedEyeIndex");
    private static readonly int SimulatedCameraCenterId = Shader.PropertyToID("_ShaderView_SimulatedCameraCenter");
    private const string OverlayVisibilityProperty = "Global_Overlay_Visibility";
    private const int CameraMaskUi = 0x20;

    [Serializable]
    private sealed class CustomGlobal
    {
        public string Name = "";
        public float Value;
    }

    // O que define uma captura. Fica em UserSettings, por usuário e fora da cena.
    [Serializable]
    private sealed class CaptureProfile
    {
        public string Name = "";
        public int StationCount = 10;
        public float FirstDistance = 1f;
        public float IntervalMeters = 1f;
        public float VerticalFov = 90f;
        public int VrAspectIndex = 2;
        public float CameraHeightOffset = 0.35f;
        public float OrbitAngleDegrees;
        public float EyeSeparationMeters = 0.064f;
        public float ConvergenceDistanceMeters = 3f;
        public float PreviewImageHeight = 300f;
        public int MonoColumns = 4;
        public bool AutoRefresh = true;
        public int RefreshRate;
        public bool SimulatedVr;
        public bool SimulatedMirror;
        public bool SimulatedPersonalMirror;
        public bool SimulatedCameraUi;
        public int SimulatedCameraMode;
        public int SimulatedEyeIndex;
        public int SimulatedScreenHeight = 1080;
        public float PimaxVerticalFov = 110f;
        public int PimaxAspectIndex = 2;
        public float PimaxEyeCantDegrees = 5f;
        public float PimaxProjectionAsymmetry = 0.06f;
        public int Ambient;
        public List<CustomGlobal> CustomGlobals = new List<CustomGlobal>();
    }

    [Serializable]
    private sealed class SavedSettings
    {
        public CaptureProfile Current = new CaptureProfile();
        public List<CaptureProfile> Presets = new List<CaptureProfile>();
    }

    private static List<CustomGlobal> activeCustomGlobals = new List<CustomGlobal>();

    private SavedSettings savedSettings;
    private List<CustomGlobal> customGlobals = new List<CustomGlobal>();
    private bool temporarySettings;
    private string presetName = "";
    private int ambient;
    private double lastCaptureMilliseconds;
    private string shaderGlobalsText = "";
    private Shader shaderGlobalsFor;
    private List<string> shaderGlobalsNames = new List<string>();
    private string shaderGlobalsLanguage;

    private readonly List<CameraStation> stations = new List<CameraStation>();
    private GameObject targetObject;
    private Material material;
    private bool materialManuallySelected;
    private Vector2 cameraScroll;
    private readonly Dictionary<int, float> eyeImageWidths = new Dictionary<int, float>();
    private Vector2 settingsScroll;
    private int stationCount = 10;
    private float firstDistance = 1f;
    private float intervalMeters = 1f;
    private float verticalFov = 90f;
    private int vrAspectIndex = 2;
    private float cameraHeightOffset = 0.35f;
    private float orbitAngleDegrees;
    private float eyeSeparationMeters = 0.064f;
    private float convergenceDistanceMeters = 3f;
    private float settingsPanelWidth = 390f;
    private float previewImageHeight = 300f;
    private int monoColumns = 4;
    private bool autoRefresh = true;
    private int refreshRate;
    private bool simulatedVr;
    private bool simulatedMirror;
    private bool simulatedPersonalMirror;
    private bool simulatedCameraUi;
    private int simulatedCameraMode;
    private int simulatedEyeIndex;
    private int simulatedScreenHeight = 1080;
    private int selectedTab;
    private Vector2 pimaxSettingsScroll;
    private Vector2 pimaxImageScroll;
    private float pimaxVerticalFov = 110f;
    private int pimaxAspectIndex = 2;
    private float pimaxEyeCantDegrees = 5f;
    private float pimaxProjectionAsymmetry = 0.06f;
    private bool captureRequested;
    private double nextAutoRefreshTime;
    // Nulo até a primeira mensagem, para o texto inicial seguir o idioma.
    private string captureStatus;
    private Camera sceneViewCamera;
    private Camera leftSceneVrCamera;
    private Camera rightSceneVrCamera;
    private RenderTexture leftSceneVrImage;
    private RenderTexture rightSceneVrImage;
    private RenderTexture pimaxStereoImage;
    private bool resizingSettingsPanel;
    private Shader checkedShader;
    private Hash128 checkedShaderHash;
    private bool checkedShaderHasSimulation;

    private sealed class ShaderContextState
    {
        private readonly float mirrorMode;
        private readonly Vector4 mirrorCameraPos;
        private readonly float faceMirrorMode;
        private readonly float cameraMode;
        private readonly int cameraMask;
        private readonly float simulatedVr;
        private readonly float simulatedEyeIndex;
        private readonly Vector4 simulatedCameraCenter;
        private readonly List<(int id, float value)> customValues;

        public ShaderContextState()
        {
            customValues = activeCustomGlobals
                .Where(global => !string.IsNullOrWhiteSpace(global.Name))
                .Select(global => Shader.PropertyToID(global.Name.Trim()))
                .Select(id => (id, Shader.GetGlobalFloat(id)))
                .ToList();
            mirrorMode = Shader.GetGlobalFloat(MirrorModeId);
            mirrorCameraPos = Shader.GetGlobalVector(MirrorCameraPosId);
            faceMirrorMode = Shader.GetGlobalFloat(FaceMirrorModeId);
            cameraMode = Shader.GetGlobalFloat(CameraModeId);
            cameraMask = Shader.GetGlobalInteger(CameraMaskId);
            simulatedVr = Shader.GetGlobalFloat(SimulatedVrId);
            simulatedEyeIndex = Shader.GetGlobalFloat(SimulatedEyeIndexId);
            simulatedCameraCenter = Shader.GetGlobalVector(SimulatedCameraCenterId);
        }

        public void Restore()
        {
            foreach (var (id, value) in customValues)
            {
                Shader.SetGlobalFloat(id, value);
            }
            Shader.SetGlobalFloat(MirrorModeId, mirrorMode);
            Shader.SetGlobalVector(MirrorCameraPosId, mirrorCameraPos);
            Shader.SetGlobalFloat(FaceMirrorModeId, faceMirrorMode);
            Shader.SetGlobalFloat(CameraModeId, cameraMode);
            Shader.SetGlobalInteger(CameraMaskId, cameraMask);
            Shader.SetGlobalFloat(SimulatedVrId, simulatedVr);
            Shader.SetGlobalFloat(SimulatedEyeIndexId, simulatedEyeIndex);
            Shader.SetGlobalVector(SimulatedCameraCenterId, simulatedCameraCenter);
        }

        public bool IsCurrent()
        {
            return Mathf.Approximately(Shader.GetGlobalFloat(MirrorModeId), mirrorMode)
                && Shader.GetGlobalVector(MirrorCameraPosId) == mirrorCameraPos
                && Mathf.Approximately(Shader.GetGlobalFloat(FaceMirrorModeId), faceMirrorMode)
                && Mathf.Approximately(Shader.GetGlobalFloat(CameraModeId), cameraMode)
                && Shader.GetGlobalInteger(CameraMaskId) == cameraMask
                && Mathf.Approximately(Shader.GetGlobalFloat(SimulatedVrId), simulatedVr)
                && Mathf.Approximately(Shader.GetGlobalFloat(SimulatedEyeIndexId), simulatedEyeIndex)
                && Shader.GetGlobalVector(SimulatedCameraCenterId) == simulatedCameraCenter;
        }
    }

    private sealed class SavedLayer
    {
        public GameObject Object;
        public int Layer;
    }

    private sealed class CameraStation
    {
        public int Index;
        public float Distance;
        public Camera Camera;
        public Camera BackgroundCamera;
        public Light DepthLight;
        public RenderTexture Image;
    }

    [MenuItem("Tools/Dosker Tools/ShaderView/VR Camera Debugger")]
    public static void OpenWindow()
    {
        var version = PackageInfo.FindForAssembly(typeof(ShaderViewVrDebuggerWindow).Assembly)?.version;
        var window = GetWindow<ShaderViewVrDebuggerWindow>(version == null ? "VR Camera Debugger" : $"VR Camera Debugger v{version}");
        window.minSize = new Vector2(640f, 400f);
        window.FindDefaultAssets();
        window.Show();
    }

    private void LoadSettings()
    {
        savedSettings = DoskerSettings<SavedSettings>.Load(SettingsFile);
        ApplyProfile(savedSettings.Current);
    }

    // A validação não pode depender nem sobrescrever o que o usuário salvou.
    private void UseDefaultSettings()
    {
        temporarySettings = true;
        ApplyProfile(new CaptureProfile());
    }

    private void SaveSettings()
    {
        if (temporarySettings)
        {
            return;
        }
        savedSettings.Current = CurrentProfile();
        DoskerSettings<SavedSettings>.Save(SettingsFile, savedSettings);
    }

    private CaptureProfile CurrentProfile()
    {
        return new CaptureProfile
        {
            StationCount = stationCount,
            FirstDistance = firstDistance,
            IntervalMeters = intervalMeters,
            VerticalFov = verticalFov,
            VrAspectIndex = vrAspectIndex,
            CameraHeightOffset = cameraHeightOffset,
            OrbitAngleDegrees = orbitAngleDegrees,
            EyeSeparationMeters = eyeSeparationMeters,
            ConvergenceDistanceMeters = convergenceDistanceMeters,
            PreviewImageHeight = previewImageHeight,
            MonoColumns = monoColumns,
            AutoRefresh = autoRefresh,
            RefreshRate = refreshRate,
            SimulatedVr = simulatedVr,
            SimulatedMirror = simulatedMirror,
            SimulatedPersonalMirror = simulatedPersonalMirror,
            SimulatedCameraUi = simulatedCameraUi,
            SimulatedCameraMode = simulatedCameraMode,
            SimulatedEyeIndex = simulatedEyeIndex,
            SimulatedScreenHeight = simulatedScreenHeight,
            PimaxVerticalFov = pimaxVerticalFov,
            PimaxAspectIndex = pimaxAspectIndex,
            PimaxEyeCantDegrees = pimaxEyeCantDegrees,
            PimaxProjectionAsymmetry = pimaxProjectionAsymmetry,
            Ambient = ambient,
            CustomGlobals = customGlobals.Select(global => new CustomGlobal { Name = global.Name, Value = global.Value }).ToList(),
        };
    }

    private void ApplyProfile(CaptureProfile profile)
    {
        stationCount = profile.StationCount;
        firstDistance = profile.FirstDistance;
        intervalMeters = profile.IntervalMeters;
        verticalFov = profile.VerticalFov;
        vrAspectIndex = profile.VrAspectIndex;
        cameraHeightOffset = profile.CameraHeightOffset;
        orbitAngleDegrees = profile.OrbitAngleDegrees;
        eyeSeparationMeters = profile.EyeSeparationMeters;
        convergenceDistanceMeters = profile.ConvergenceDistanceMeters;
        previewImageHeight = profile.PreviewImageHeight;
        monoColumns = profile.MonoColumns;
        autoRefresh = profile.AutoRefresh;
        refreshRate = profile.RefreshRate;
        simulatedVr = profile.SimulatedVr;
        simulatedMirror = profile.SimulatedMirror;
        simulatedPersonalMirror = profile.SimulatedPersonalMirror;
        simulatedCameraUi = profile.SimulatedCameraUi;
        simulatedCameraMode = profile.SimulatedCameraMode;
        simulatedEyeIndex = profile.SimulatedEyeIndex;
        simulatedScreenHeight = profile.SimulatedScreenHeight;
        pimaxVerticalFov = profile.PimaxVerticalFov;
        pimaxAspectIndex = profile.PimaxAspectIndex;
        pimaxEyeCantDegrees = profile.PimaxEyeCantDegrees;
        pimaxProjectionAsymmetry = profile.PimaxProjectionAsymmetry;
        ambient = profile.Ambient;
        customGlobals = profile.CustomGlobals.Select(global => new CustomGlobal { Name = global.Name, Value = global.Value }).ToList();
    }

    private void OnEnable()
    {
        wantsMouseMove = true;
        LoadSettings();
        FindDefaultAssets();
        captureRequested = CanCapture();
        SceneView.duringSceneGui += DrawCameraPositions;
        SceneView.duringSceneGui += HandleSceneViewRepaint;
        EditorApplication.update += HandleEditorUpdate;
    }

    private void OnDisable()
    {
        SaveSettings();
        SceneView.duringSceneGui -= DrawCameraPositions;
        SceneView.duringSceneGui -= HandleSceneViewRepaint;
        EditorApplication.update -= HandleEditorUpdate;
        DestroyCameraStations();
        DestroySceneVrCameras();
    }

    private void FindDefaultAssets()
    {
        if (targetObject == null)
        {
            var target = GameObject.Find(TestObjectName);
            if (target != null)
            {
                targetObject = target;
            }
        }
        ResolveTargetMaterial();
    }

    private void ResolveTargetMaterial()
    {
        if (targetObject == null)
        {
            return;
        }

        if (materialManuallySelected && IsUsableMaterial(material))
        {
            return;
        }

        var targetMaterial = GetFirstTargetMaterial(targetObject);
        if (material != targetMaterial)
        {
            material = targetMaterial;
            captureRequested = true;
        }
        materialManuallySelected = false;
    }

    private static Material GetFirstTargetMaterial(GameObject target)
    {
        if (target == null)
        {
            return null;
        }

        foreach (var renderer in target.GetComponentsInChildren<Renderer>(true))
        {
            var sharedMaterials = renderer.sharedMaterials;
            if (sharedMaterials.Length > 0 && sharedMaterials[0] != null)
            {
                return sharedMaterials[0];
            }
        }
        return null;
    }

    private static bool IsUsableMaterial(Material candidate)
    {
        return candidate != null
            && candidate.shader != null
            && !ShaderUtil.ShaderHasError(candidate.shader)
            && candidate.shader.isSupported;
    }

    private void HandleEditorUpdate()
    {
        if (autoRefresh
            && refreshRate > 0
            && (selectedTab == 1 || stations.Count > 0)
            && EditorApplication.timeSinceStartup >= nextAutoRefreshTime)
        {
            captureRequested = true;
            nextAutoRefreshTime = EditorApplication.timeSinceStartup + 1.0 / refreshRate;
        }
        if (captureRequested)
        {
            Repaint();
        }
    }

    private void HandleSceneViewRepaint(SceneView sceneView)
    {
        if (!autoRefresh
            || refreshRate != 0
            || (selectedTab == 0 && stations.Count == 0)
            || targetObject == null
            || Event.current == null
            || Event.current.type != EventType.Repaint)
        {
            return;
        }

        captureRequested = true;
        Repaint();
    }

    private void OnGUI()
    {
        DrawToolbar();
        var nextTab = GUILayout.Toolbar(selectedTab, TabLabels);
        if (nextTab != selectedTab)
        {
            selectedTab = nextTab;
            captureRequested = true;
        }
        using (new EditorGUILayout.HorizontalScope())
        {
            if (selectedTab == 0)
            {
                DrawMaterialPanel();
                DrawSettingsSplitter();
                DrawCameraPanel();
            }
            else
            {
                DrawPimaxSettingsPanel();
                DrawSettingsSplitter();
                DrawPimaxImagePanel();
            }
        }

        if (captureRequested)
        {
            captureRequested = false;
            if (CanCapture())
            {
                CaptureAllStations();
            }
        }
    }

    private void DrawToolbar()
    {
        using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
        {
            GUILayout.Label("ShaderView · debugger", EditorStyles.boldLabel);
            GUILayout.Space(6f);
            var complete = stations.Count >= stationCount;
            DoskerEditorUi.StatusPill(
                string.Format(Tr("{0} / {1} capturas{2}", "{0} / {1} captures{2}"), stations.Count, stationCount, autoRefresh ? Tr(" · contínua", " · continuous") : string.Empty),
                complete ? DoskerEditorUi.Green : DoskerEditorUi.Yellow,
                16f);
            if (lastCaptureMilliseconds > 0.0)
            {
                GUILayout.Space(6f);
                GUILayout.Label(string.Format("{0:0.0} ms · {1:0} MB", lastCaptureMilliseconds, CaptureMemoryMegabytes()), EditorStyles.miniLabel);
            }
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(DoskerEditorUi.Content(Tr(" Abrir cena de teste", " Open test scene"), "SceneAsset Icon"), EditorStyles.toolbarButton))
            {
                OpenPreviewScene();
            }
            if (GUILayout.Button(DoskerEditorUi.Content(Tr(" Localizar objeto do shader", " Find shader object"), "Search Icon"), EditorStyles.toolbarButton))
            {
                FindDefaultAssets();
                if (targetObject != null)
                {
                    Selection.activeGameObject = targetObject;
                    EditorGUIUtility.PingObject(targetObject);
                }
            }
            DoskerEditorUi.LanguageButton();
        }
    }

    private float CaptureMemoryMegabytes()
    {
        // RGBA8 mais depth de 32 bits, sem contar mipmaps.
        var pixels = stations.Where(station => station.Image != null).Sum(station => (long)station.Image.width * station.Image.height);
        return pixels * 8L / (1024f * 1024f);
    }

    private void DrawProfilesCard()
    {
        using (var card = new DoskerEditorUi.CardScope("Debugger.Profiles", Tr("Perfis e globais", "Profiles and globals"), "SaveAs", DoskerEditorUi.Purple, defaultExpanded: false))
        {
            if (!card.Expanded)
            {
                return;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                presetName = EditorGUILayout.TextField(presetName);
                using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(presetName)))
                {
                    if (GUILayout.Button(Tr("Salvar", "Save"), GUILayout.Width(60f)))
                    {
                        var profile = CurrentProfile();
                        profile.Name = presetName.Trim();
                        savedSettings.Presets.RemoveAll(existing => existing.Name == profile.Name);
                        savedSettings.Presets.Add(profile);
                    }
                }
            }
            foreach (var preset in savedSettings.Presets.ToList())
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(preset.Name, EditorStyles.linkLabel))
                    {
                        ApplyProfile(preset);
                        captureRequested = true;
                    }
                    if (GUILayout.Button(Tr("Remover", "Remove"), EditorStyles.miniButton, GUILayout.Width(60f)))
                    {
                        savedSettings.Presets.Remove(preset);
                    }
                }
            }

            EditorGUILayout.Space(4f);
            var nextAmbient = EditorGUILayout.Popup(Tr("Luz ambiente", "Ambient light"), ambient, AmbientLabels);
            if (nextAmbient != ambient)
            {
                ambient = nextAmbient;
                captureRequested = true;
            }

            EditorGUILayout.LabelField(Tr("Globais extras, aplicadas só durante a captura", "Extra globals, applied only during the capture"), EditorStyles.miniLabel);
            foreach (var global in customGlobals.ToList())
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUI.BeginChangeCheck();
                    global.Name = EditorGUILayout.TextField(global.Name);
                    global.Value = EditorGUILayout.FloatField(global.Value, GUILayout.Width(60f));
                    captureRequested |= EditorGUI.EndChangeCheck();
                    if (GUILayout.Button("×", EditorStyles.miniButton, GUILayout.Width(22f)))
                    {
                        customGlobals.Remove(global);
                        captureRequested = true;
                    }
                }
            }
            if (GUILayout.Button(Tr("Adicionar global", "Add global")))
            {
                customGlobals.Add(new CustomGlobal());
            }
        }
    }

    private void DrawStatus()
    {
        using (var card = new DoskerEditorUi.CardScope(
            "Debugger.About",
            Tr("Sobre as capturas", "About the captures"),
            "_Help",
            DoskerEditorUi.Neutral,
            defaultExpanded: false))
        {
            if (!card.Expanded)
            {
                return;
            }

            var depthLightCount = Mathf.Max(0, stationCount - 1);
            EditorGUILayout.HelpBox(
                Tr("A prévia VR única acompanha a câmera ativa da Scene View. As capturas adicionais são mono e ficam nas distâncias configuradas, " +
                   "todas usando o mesmo perfil de contexto acima. A referência renderiza o mundo sem o objeto-alvo e depois captura só o alvo " +
                   "com a profundidade do mundo isolada; as demais usam o mesmo processo com DepthLight ativa, " +
                   "intensidade 0,01, sombras Hard e modo Baked.",
                   "The single VR preview follows the active Scene View camera. The extra captures are mono and sit at the configured distances, " +
                   "all using the same context profile above. The reference renders the world without the target object, then captures only the target " +
                   "with the world depth isolated; the others use the same process with DepthLight on, " +
                   "intensity 0.01, Hard shadows and Baked mode."),
                MessageType.Info);

            EditorGUILayout.LabelField(
                string.Format(
                    Tr("Distâncias: {0} · início: {1:0.##} m · intervalo: {2:0.##} m · VR Scene View: 1 · sem depth/DepthLight: 1 · com DepthLight: {3}",
                       "Distances: {0} · start: {1:0.##} m · interval: {2:0.##} m · VR Scene View: 1 · without depth/DepthLight: 1 · with DepthLight: {3}"),
                    stationCount,
                    firstDistance,
                    intervalMeters,
                    depthLightCount),
                EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.HelpBox(
                Tr("A DepthLight não cria mapa de profundidade: a depth texture vem da geometria. A simulação controla as ramificações do shader, " +
                   "mas não substitui o runtime XR/VRChat.",
                   "The DepthLight doesn't create a depth map: the depth texture comes from the geometry. The simulation drives the shader branches, " +
                   "but doesn't replace the XR/VRChat runtime."),
                MessageType.Warning);
        }
    }

    private void DrawCaptureFooter(string captureLabel)
    {
        DoskerEditorUi.HorizontalLine();
        using (DoskerEditorUi.ActionBar())
        {
            if (DoskerEditorUi.PrimaryButton(captureLabel, "Camera Icon", DoskerEditorUi.Blue))
            {
                captureRequested = true;
            }
            EditorGUILayout.Space(2f);
            autoRefresh = EditorGUILayout.ToggleLeft(Tr("Atualização contínua", "Continuous refresh"), autoRefresh);
            if (autoRefresh)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(Tr("FPS da captura", "Capture FPS"), GUILayout.Width(100f));
                    refreshRate = EditorGUILayout.IntPopup(
                        refreshRate,
                        new[] { Tr("Nativo (Scene View)", "Native (Scene View)"), "2", "5", "10", "15", "30", "60" },
                        new[] { 0, 2, 5, 10, 15, 30, 60 });
                }
            }
        }
    }

    private void DrawMaterialPanel()
    {
        using (new EditorGUILayout.VerticalScope(GUILayout.Width(settingsPanelWidth), GUILayout.ExpandHeight(true)))
        {
            settingsScroll = EditorGUILayout.BeginScrollView(settingsScroll, GUILayout.ExpandHeight(true));
            DrawTargetCard();

            EditorGUI.BeginChangeCheck();
            using (var card = new DoskerEditorUi.CardScope("Debugger.Cameras", Tr("Câmeras e distâncias", "Cameras and distances"), "Camera Icon", DoskerEditorUi.Blue))
            {
                if (card.Expanded)
                {
                    stationCount = EditorGUILayout.IntSlider(Tr("Quantidade de distâncias", "Number of distances"), stationCount, 1, 24);
                    firstDistance = EditorGUILayout.Slider(Tr("Primeira distância (m)", "First distance (m)"), firstDistance, 0.1f, 20f);
                    intervalMeters = EditorGUILayout.Slider(Tr("Intervalo entre câmeras (m)", "Interval between cameras (m)"), intervalMeters, 0.1f, 5f);
                    cameraHeightOffset = EditorGUILayout.Slider(Tr("Altura relativa (m)", "Relative height (m)"), cameraHeightOffset, -2f, 3f);
                    orbitAngleDegrees = EditorGUILayout.Slider(Tr("Rotação em volta do alvo (°)", "Rotation around the target (°)"), orbitAngleDegrees, 0f, 360f);
                    verticalFov = EditorGUILayout.Slider(Tr("FOV das câmeras (°)", "Camera FOV (°)"), verticalFov, 30f, 120f);
                    vrAspectIndex = EditorGUILayout.Popup(Tr("Proporção da prévia VR", "VR preview aspect ratio"), vrAspectIndex, VrAspectLabels);
                    eyeSeparationMeters = EditorGUILayout.Slider(Tr("IPD estéreo (m)", "Stereo IPD (m)"), eyeSeparationMeters, 0.04f, 0.08f);
                    convergenceDistanceMeters = EditorGUILayout.Slider(Tr("Convergência estéreo (m)", "Stereo convergence (m)"), convergenceDistanceMeters, 0.5f, 20f);
                }
            }

            using (var card = new DoskerEditorUi.CardScope("Debugger.Context", Tr("Contexto simulado", "Simulated context"), "SettingsIcon", DoskerEditorUi.Purple))
            {
                if (card.Expanded)
                {
                    simulatedVr = EditorGUILayout.Toggle(Tr("É VR", "Is VR"), simulatedVr);
                    using (new EditorGUI.DisabledScope(!simulatedVr))
                    {
                        simulatedEyeIndex = EditorGUILayout.Popup(Tr("Olho simulado", "Simulated eye"), simulatedEyeIndex, new[] { Tr("Esquerdo", "Left"), Tr("Direito", "Right") });
                    }
                    simulatedMirror = EditorGUILayout.Toggle(Tr("É espelho", "Is mirror"), simulatedMirror);
                    simulatedPersonalMirror = EditorGUILayout.Toggle(Tr("É espelho pessoal", "Is personal mirror"), simulatedPersonalMirror);
                    simulatedCameraMode = EditorGUILayout.Popup(
                        Tr("Tipo de câmera", "Camera type"),
                        simulatedCameraMode,
                        new[] { "Regular", Tr("Câmera VR de mão", "VR handheld camera"), Tr("Câmera desktop de mão", "Desktop handheld camera"), Tr("Foto / screenshot", "Photo / screenshot") });
                    simulatedCameraUi = EditorGUILayout.Toggle(Tr("UI visível na câmera", "UI visible to the camera"), simulatedCameraUi);
                    DrawContextWarnings();
                }
            }

            using (var card = new DoskerEditorUi.CardScope("Debugger.Output", Tr("Resolução e prévia", "Resolution and preview"), "RawImage Icon", DoskerEditorUi.Green))
            {
                if (card.Expanded)
                {
                    simulatedScreenHeight = EditorGUILayout.IntPopup(
                        Tr("Resolução (altura simulada)", "Resolution (simulated height)"),
                        simulatedScreenHeight,
                        new[] { Tr("720 (prévia da câmera)", "720 (camera preview)"), "1080", "1440", "2160 (4K)" },
                        new[] { 720, 1080, 1440, 2160 });
                    monoColumns = EditorGUILayout.IntSlider(Tr("Colunas mono por linha", "Mono columns per row"), monoColumns, 1, 8);
                    previewImageHeight = EditorGUILayout.Slider(Tr("Altura das prévias (px)", "Preview height (px)"), previewImageHeight, 100f, 800f);
                }
            }
            if (EditorGUI.EndChangeCheck())
            {
                captureRequested = true;
            }

            DrawStatus();
            EditorGUILayout.EndScrollView();
            DrawCaptureFooter(Tr("Capturar todas", "Capture all"));
        }
    }

    private void DrawContextWarnings()
    {
        if (material != null && material.HasProperty(OverlayVisibilityProperty) && !ShaderHasVrSimulation(material.shader))
        {
            EditorGUILayout.HelpBox(
                Tr("Este shader é anterior à simulação de VR: sem " + SimulatedVrVariable + ", a visão principal continua igual à de desktop. " +
                   "Atualize o shader para testar os filtros por VR e por olho fora de espelho.",
                   "This shader predates the VR simulation: without " + SimulatedVrVariable + ", the main view stays the same as desktop. " +
                   "Update the shader to test the VR and per-eye filters outside mirrors."),
                MessageType.Warning);
        }
        if (!simulatedMirror && !ShouldSimulateVr(true))
        {
            EditorGUILayout.HelpBox(
                Tr("A simulação de VR fica desligada em espelho pessoal, câmera de mão e foto: nada confirma que esses renders são stereo.", "The VR simulation is off in personal mirrors, handheld cameras and photos: nothing confirms those renders are stereo."),
                MessageType.Info);
        }
        if (simulatedCameraMode == 1 && !simulatedMirror)
        {
            EditorGUILayout.HelpBox(
                Tr("Câmera VR de mão segue o filtro Camera_Desktop: o filtro Camera_VR fica dentro de isVR(), que é falso em câmera não stereo.", "The VR handheld camera follows the Camera_Desktop filter: the Camera_VR filter sits inside isVR(), which is false on a non-stereo camera."),
                MessageType.Info);
        }
        if (material != null
            && material.HasProperty(OverlayVisibilityProperty)
            && material.GetFloat(OverlayVisibilityProperty) < 0.5f)
        {
            EditorGUILayout.HelpBox(
                Tr("O material está com Overlay/Visibility desligado: os filtros por contexto (espelho, câmera, olho) não são avaliados, " +
                   "então mudar o contexto não altera o overlay.",
                   "The material has Overlay/Visibility off: the context filters (mirror, camera, eye) aren't evaluated, " +
                   "so changing the context doesn't change the overlay."),
                MessageType.Warning);
        }
    }

    // Só o arquivo principal: includes não entram, então a lista pode estar incompleta.
    private string ShaderGlobalsText(Shader shader)
    {
        if (shader != shaderGlobalsFor)
        {
            shaderGlobalsFor = shader;
            var assetPath = AssetDatabase.GetAssetPath(shader);
            var names = File.Exists(assetPath)
                ? VrChatGlobal.Matches(File.ReadAllText(assetPath)).Cast<Match>().Select(match => match.Value).Distinct().OrderBy(name => name).ToList()
                : new List<string>();
            shaderGlobalsNames = names;
            shaderGlobalsLanguage = null;
        }
        if (shaderGlobalsLanguage != ShortCode)
        {
            shaderGlobalsLanguage = ShortCode;
            shaderGlobalsText = shaderGlobalsNames.Count == 0
                ? Tr("O arquivo do shader não cita globais do VRChat, então o contexto simulado pode não mudar nada.", "The shader file doesn't mention any VRChat globals, so the simulated context may change nothing.")
                : Tr("O shader lê: ", "The shader reads: ") + string.Join(", ", shaderGlobalsNames);
        }
        return shaderGlobalsText;
    }

    private bool ShaderHasVrSimulation(Shader shader)
    {
        // A global não aparece nas propriedades do shader; só o fonte diz se ele a usa.
        var assetPath = AssetDatabase.GetAssetPath(shader);
        var hash = AssetDatabase.GetAssetDependencyHash(assetPath);
        if (shader != checkedShader || hash != checkedShaderHash)
        {
            checkedShader = shader;
            checkedShaderHash = hash;
            checkedShaderHasSimulation = File.Exists(assetPath)
                && File.ReadAllText(assetPath).Contains(SimulatedVrVariable);
        }
        return checkedShaderHasSimulation;
    }

    private void DrawTargetCard()
    {
        using (var card = new DoskerEditorUi.CardScope("Debugger.Target", Tr("Alvo", "Target"), "GameObject Icon", DoskerEditorUi.Cyan))
        {
            if (!card.Expanded)
            {
                return;
            }

            var nextTarget = (GameObject)EditorGUILayout.ObjectField(
                Tr("Objeto do shader", "Shader object"),
                targetObject,
                typeof(GameObject),
                true);
            if (nextTarget != targetObject)
            {
                targetObject = nextTarget;
                ResolveTargetMaterial();
                captureRequested = true;
            }

            using (new EditorGUI.DisabledScope(Selection.activeGameObject == null))
            {
                if (GUILayout.Button(Tr("Usar a seleção como alvo", "Use the selection as target")))
                {
                    targetObject = Selection.activeGameObject;
                    ResolveTargetMaterial();
                    captureRequested = true;
                }
            }

            var nextMaterial = (Material)EditorGUILayout.ObjectField(
                Tr("Material no Inspector (opcional)", "Inspector material (optional)"),
                material,
                typeof(Material),
                false);
            if (nextMaterial != material)
            {
                material = nextMaterial;
                materialManuallySelected = material != null;
                ResolveTargetMaterial();
                captureRequested = true;
            }

            if (material != null && material.shader != null)
            {
                EditorGUILayout.LabelField(ShaderGlobalsText(material.shader), EditorStyles.wordWrappedMiniLabel);
            }

            if (material == null)
            {
                EditorGUILayout.HelpBox(
                    Tr("Sem material para abrir no Inspector. As capturas continuam usando os materiais do objeto.", "No material to open in the Inspector. The captures keep using the object's materials."),
                    MessageType.None);
            }
            else if (!IsUsableMaterial(material))
            {
                EditorGUILayout.HelpBox(
                    Tr("O material do Inspector está inválido. As capturas não são bloqueadas; será usado o primeiro material do objeto, se disponível.", "The Inspector material is invalid. Captures aren't blocked; the object's first material will be used, if available."),
                    MessageType.Warning);
            }

            EditorGUILayout.HelpBox(
                Tr("O inspector original edita as propriedades do material e do shader; ele não controla as câmeras. " +
                   "Abra-o no Inspector normal para preservar espaço para as imagens.",
                   "The original inspector edits the material and shader properties; it doesn't control the cameras. " +
                   "Open it in the regular Inspector to keep room for the images."),
                MessageType.None);
            DrawSelectMaterialButton();
        }
        DrawProfilesCard();
    }

    private void DrawSelectMaterialButton()
    {
        // Abrir o material não é uma alteração de parâmetro: não dispara nova captura.
        var previousChanged = GUI.changed;
        using (new EditorGUI.DisabledScope(material == null))
        {
            if (GUILayout.Button(DoskerEditorUi.Content(Tr(" Selecionar material no Inspector do Unity", " Select material in the Unity Inspector"), "Material Icon")))
            {
                Selection.activeObject = material;
                EditorGUIUtility.PingObject(material);
                EditorApplication.ExecuteMenuItem("Window/General/Inspector");
            }
        }
        GUI.changed = previousChanged;
    }

    private void DrawCameraPanel()
    {
        using (new EditorGUILayout.VerticalScope(GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true)))
        {
            cameraScroll = EditorGUILayout.BeginScrollView(cameraScroll, GUILayout.ExpandHeight(true));
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(Tr("Prévia VR estéreo · câmera da Scene View", "Stereo VR preview · Scene View camera"), EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                DoskerEditorUi.Badge(string.Format("IPD {0:0} mm", eyeSeparationMeters * 1000f), DoskerEditorUi.Blue);
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                DrawEyeImage(Tr("OLHO ESQUERDO · Scene View", "LEFT EYE · Scene View"), leftSceneVrImage, previewImageHeight);
                DrawEyeImage(Tr("OLHO DIREITO · Scene View", "RIGHT EYE · Scene View"), rightSceneVrImage, previewImageHeight);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(Tr("Capturas mono por distância", "Mono captures by distance"), EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                DoskerEditorUi.Badge(string.Format("{0} / {1}", stations.Count, stationCount), DoskerEditorUi.Blue);
            }

            if (stations.Count == 0)
            {
                EditorGUILayout.HelpBox(captureStatus ?? Tr("Configure as câmeras e clique em Capturar todas.", "Set up the cameras and click Capture all."), MessageType.None);
            }

            var columnCount = Mathf.Clamp(monoColumns, 1, 8);
            for (var rowStart = 0; rowStart < stations.Count; rowStart += columnCount)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    var rowEnd = Mathf.Min(rowStart + columnCount, stations.Count);
                    for (var stationIndex = rowStart; stationIndex < rowEnd; stationIndex++)
                    {
                        DrawStation(stations[stationIndex]);
                    }
                }
            }
            EditorGUILayout.EndScrollView();
        }
    }

    private void DrawStation(CameraStation station)
    {
        using (new DoskerEditorUi.PanelScope(station.Index == 0 ? DoskerEditorUi.Yellow : DoskerEditorUi.Blue))
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                DoskerEditorUi.Badge(string.Format("{0:0.##} m", station.Distance), DoskerEditorUi.Blue);
                if (station.Index == 0)
                {
                    DoskerEditorUi.Badge(Tr("sem depth", "no depth"), DoskerEditorUi.Yellow);
                }
            }
            DrawEyeImage(Tr("Contexto selecionado · mono", "Selected context · mono"), station.Image, Mathf.Min(previewImageHeight * 0.6f, 220f));
        }
    }

    private void DrawPimaxSettingsPanel()
    {
        using (new EditorGUILayout.VerticalScope(GUILayout.Width(settingsPanelWidth), GUILayout.ExpandHeight(true)))
        {
            pimaxSettingsScroll = EditorGUILayout.BeginScrollView(pimaxSettingsScroll, GUILayout.ExpandHeight(true));
            DrawPimaxSettings();
            EditorGUILayout.Space(2f);
            EditorGUILayout.HelpBox(
                Tr("Renderiza os dois olhos em uma única imagem lado a lado, com FOV, inclinação e projeção assimétrica ajustáveis. " +
                   "É uma aproximação visual: não acessa o perfil óptico nem as matrizes reais do SteamVR/Pimax.",
                   "Renders both eyes into one side-by-side image, with adjustable FOV, canting and asymmetric projection. " +
                   "It's a visual approximation: it doesn't access the optical profile or the real SteamVR/Pimax matrices."),
                MessageType.Warning);
            EditorGUILayout.EndScrollView();
            DrawCaptureFooter(Tr("Capturar estéreo", "Capture stereo"));
        }
    }

    private void DrawPimaxImagePanel()
    {
        using (new EditorGUILayout.VerticalScope(GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true)))
        {
            pimaxImageScroll = EditorGUILayout.BeginScrollView(pimaxImageScroll, GUILayout.ExpandHeight(true));
            if (pimaxStereoImage != null && pimaxStereoImage.IsCreated())
            {
                DrawEyeImage(Tr("Resultado estereoscópico · olho esquerdo | olho direito", "Stereoscopic result · left eye | right eye"), pimaxStereoImage, previewImageHeight);
            }
            else
            {
                EditorGUILayout.HelpBox(Tr("Configure o perfil e clique em Capturar estéreo para gerar a imagem.", "Set up the profile and click Capture stereo to create the image."), MessageType.None);
            }
            EditorGUILayout.EndScrollView();
        }
    }

    private void DrawPimaxSettings()
    {
        var preset = EditorGUILayout.Popup(Tr("Faixa de FOV", "FOV range"), -1, StereoPresets.Select(item => item.label).ToArray());
        if (preset >= 0)
        {
            pimaxVerticalFov = StereoPresets[preset].fov;
            eyeSeparationMeters = StereoPresets[preset].ipd;
            pimaxEyeCantDegrees = StereoPresets[preset].cant;
            pimaxProjectionAsymmetry = StereoPresets[preset].asymmetry;
            captureRequested = true;
        }

        EditorGUI.BeginChangeCheck();
        using (var card = new DoskerEditorUi.CardScope("Pimax.Target", Tr("Alvo", "Target"), "GameObject Icon", DoskerEditorUi.Cyan))
        {
            if (card.Expanded)
            {
                var nextTarget = (GameObject)EditorGUILayout.ObjectField(
                    Tr("Objeto do shader", "Shader object"),
                    targetObject,
                    typeof(GameObject),
                    true);
                if (nextTarget != targetObject)
                {
                    targetObject = nextTarget;
                    ResolveTargetMaterial();
                    captureRequested = true;
                }

                var nextMaterial = (Material)EditorGUILayout.ObjectField(
                    Tr("Material no Inspector (opcional)", "Inspector material (optional)"),
                    material,
                    typeof(Material),
                    false);
                if (nextMaterial != material)
                {
                    material = nextMaterial;
                    materialManuallySelected = material != null;
                    ResolveTargetMaterial();
                    captureRequested = true;
                }
                DrawSelectMaterialButton();
            }
        }

        using (var card = new DoskerEditorUi.CardScope("Pimax.Projection", Tr("Projeção estereoscópica", "Stereoscopic projection"), "Camera Icon", DoskerEditorUi.Purple))
        {
            if (card.Expanded)
            {
                pimaxVerticalFov = EditorGUILayout.Slider(Tr("FOV vertical por olho (°)", "Vertical FOV per eye (°)"), pimaxVerticalFov, 60f, 140f);
                pimaxAspectIndex = EditorGUILayout.Popup(Tr("Proporção de cada olho", "Aspect ratio of each eye"), pimaxAspectIndex, VrAspectLabels);
                eyeSeparationMeters = EditorGUILayout.Slider("IPD (m)", eyeSeparationMeters, 0.04f, 0.08f);
                convergenceDistanceMeters = EditorGUILayout.Slider(
                    Tr("Convergência estéreo (m)", "Stereo convergence (m)"),
                    convergenceDistanceMeters,
                    0.5f,
                    20f);
                pimaxEyeCantDegrees = EditorGUILayout.Slider(Tr("Inclinação das lentes (°)", "Lens canting (°)"), pimaxEyeCantDegrees, 0f, 15f);
                pimaxProjectionAsymmetry = EditorGUILayout.Slider(
                    Tr("Assimetria horizontal por olho", "Horizontal asymmetry per eye"),
                    pimaxProjectionAsymmetry,
                    0f,
                    0.2f);
            }
        }
        if (EditorGUI.EndChangeCheck())
        {
            captureRequested = true;
        }

        using (var card = new DoskerEditorUi.CardScope("Pimax.Output", Tr("Prévia", "Preview"), "RawImage Icon", DoskerEditorUi.Green))
        {
            if (card.Expanded)
            {
                previewImageHeight = EditorGUILayout.Slider(Tr("Altura da prévia (px)", "Preview height (px)"), previewImageHeight, 100f, 800f);
            }
        }
    }

    private void DrawEyeImage(string label, RenderTexture image, float imageHeight)
    {
        using (new EditorGUILayout.VerticalScope(GUILayout.ExpandWidth(true)))
        {
            EditorGUILayout.LabelField(label, EditorStyles.centeredGreyMiniLabel);

            // imageHeight is the maximum: shrink to the width actually available so the image
            // is not letterboxed with empty vertical bands above and below it.
            var widthKey = GUIUtility.GetControlID(FocusType.Passive);
            if (image != null && image.IsCreated()
                && eyeImageWidths.TryGetValue(widthKey, out var knownWidth) && knownWidth > 1f)
            {
                imageHeight = Mathf.Min(imageHeight, knownWidth * image.height / (float)image.width);
            }

            var rect = GUILayoutUtility.GetRect(1f, imageHeight, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint
                && (!eyeImageWidths.TryGetValue(widthKey, out var previousWidth) || !Mathf.Approximately(previousWidth, rect.width)))
            {
                eyeImageWidths[widthKey] = rect.width;
                Repaint();
            }
            if (image != null && image.IsCreated())
            {
                var aspect = image.width / (float)image.height;
                var imageRect = rect;
                imageRect.width = Mathf.Min(rect.width, rect.height * aspect);
                imageRect.height = Mathf.Min(rect.height, imageRect.width / aspect);
                imageRect.x += (rect.width - imageRect.width) * 0.5f;
                imageRect.y += (rect.height - imageRect.height) * 0.5f;
                EditorGUI.DrawPreviewTexture(imageRect, image, null, ScaleMode.ScaleToFit);
            }
            else
            {
                EditorGUI.DrawRect(rect, new Color(0.08f, 0.09f, 0.11f));
            }
        }
    }

    private void DrawSettingsSplitter()
    {
        var splitterRect = GUILayoutUtility.GetRect(6f, 1f, GUILayout.ExpandHeight(true));
        EditorGUIUtility.AddCursorRect(splitterRect, MouseCursor.ResizeHorizontal);
        var controlId = GUIUtility.GetControlID(FocusType.Passive);
        var currentEvent = Event.current;
        if (currentEvent.type == EventType.MouseDown && splitterRect.Contains(currentEvent.mousePosition))
        {
            resizingSettingsPanel = true;
            GUIUtility.hotControl = controlId;
            currentEvent.Use();
        }
        else if (currentEvent.type == EventType.MouseDrag
                 && resizingSettingsPanel
                 && GUIUtility.hotControl == controlId)
        {
            settingsPanelWidth = Mathf.Clamp(settingsPanelWidth + currentEvent.delta.x, 260f, Mathf.Max(360f, position.width - 320f));
            currentEvent.Use();
            Repaint();
        }
        else if (currentEvent.type == EventType.MouseUp
                 && resizingSettingsPanel
                 && GUIUtility.hotControl == controlId)
        {
            resizingSettingsPanel = false;
            GUIUtility.hotControl = 0;
            currentEvent.Use();
        }
        EditorGUI.DrawRect(splitterRect, DoskerEditorUi.Divider);
    }

    private bool CanCapture()
    {
        if (targetObject == null)
        {
            captureStatus = Tr("Selecione o objeto do shader antes de capturar.", "Select the shader object before capturing.");
            return false;
        }
        ResolveTargetMaterial();
        return true;
    }

    private bool ShouldSimulateVr(bool vr)
    {
        // Em espelho o shader usa o caminho comum; câmeras não são stereo; o espelho pessoal não foi confirmado.
        return vr && !simulatedMirror && !simulatedPersonalMirror && simulatedCameraMode == 0;
    }

    private void SetShaderContext(bool vr, float eyeIndex, Camera camera, Vector3 headCenter)
    {
        var mirrorMode = !simulatedMirror ? 0f : vr ? 1f : 2f;
        var mirrorCameraPos = Vector3.zero;
        if (simulatedMirror)
        {
            // A posição do espelho é a do centro da cabeça, independente do olho: é o lado
            // em que ela fica na câmera que faz isRightEye() responder no espelho VR.
            var centerOffset = vr ? (eyeIndex > 0.5f ? -1f : 1f) * eyeSeparationMeters * 0.5f : 0f;
            mirrorCameraPos = camera.transform.position + camera.transform.right * centerOffset;
        }

        Shader.SetGlobalFloat(MirrorModeId, mirrorMode);
        Shader.SetGlobalVector(MirrorCameraPosId, mirrorCameraPos);
        Shader.SetGlobalFloat(FaceMirrorModeId, simulatedPersonalMirror ? 1f : 0f);
        Shader.SetGlobalFloat(CameraModeId, simulatedCameraMode);
        Shader.SetGlobalInteger(CameraMaskId, simulatedCameraMode != 0 && simulatedCameraUi ? CameraMaskUi : 0);

        var simulateVr = ShouldSimulateVr(vr);
        Shader.SetGlobalFloat(SimulatedVrId, simulateVr ? 1f : 0f);
        Shader.SetGlobalFloat(SimulatedEyeIndexId, simulateVr ? eyeIndex : 0f);
        // Com a simulação ligada o centro é obrigatório: em (0,0,0) a distância sai errada e _MaxRange corta o efeito.
        Shader.SetGlobalVector(SimulatedCameraCenterId, simulateVr ? headCenter : Vector3.zero);

        foreach (var global in customGlobals.Where(global => !string.IsNullOrWhiteSpace(global.Name)))
        {
            Shader.SetGlobalFloat(global.Name.Trim(), global.Value);
        }
    }

    private void CaptureAllStations()
    {
        // O ContextState de cada captura lê esta lista para restaurar os valores.
        activeCustomGlobals = customGlobals;
        var previousAmbient = (RenderSettings.ambientMode, RenderSettings.ambientLight);
        if (ambient > 0)
        {
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = AmbientColors[ambient];
        }

        var timer = Stopwatch.StartNew();
        try
        {
            CaptureCurrentTab();
        }
        finally
        {
            RenderSettings.ambientMode = previousAmbient.ambientMode;
            RenderSettings.ambientLight = previousAmbient.ambientLight;
        }
        lastCaptureMilliseconds = timer.Elapsed.TotalMilliseconds;
    }

    private void CaptureCurrentTab()
    {
        if (selectedTab == 1)
        {
            CapturePimaxOnly();
            return;
        }

        EnsureCameraStations();

        var previousContext = new ShaderContextState();
        var targetLayer = FindUnusedLayer();
        var savedLayers = MoveTargetToLayer(targetLayer);
        var sceneViewCaptured = false;
        try
        {
            sceneViewCaptured = CaptureSceneViewVr();
            foreach (var station in stations)
            {
                UpdateDistanceCamera(station);
                SetDepthLightActive(station, station.Index > 0);
                // Antes do fundo: outros objetos com o shader não podem herdar o contexto da prévia VR.
                SetShaderContext(simulatedVr, simulatedEyeIndex, station.Camera, station.Camera.transform.position);
                if (station.Index == 0)
                {
                    var targetLayerMask = 1 << targetLayer;
                    station.BackgroundCamera.cullingMask = ~targetLayerMask;
                    station.BackgroundCamera.clearFlags = CameraClearFlags.Skybox;
                    station.BackgroundCamera.targetTexture = station.Image;
                    station.BackgroundCamera.Render();

                    station.Camera.cullingMask = targetLayerMask;
                    station.Camera.clearFlags = CameraClearFlags.Depth;
                }
                else
                {
                    station.Camera.cullingMask = ~0;
                    station.Camera.clearFlags = CameraClearFlags.Skybox;
                }
                station.Camera.Render();
                SetDepthLightActive(station, false);
            }
        }
        finally
        {
            RestoreLayers(savedLayers);
            previousContext.Restore();
            foreach (var station in stations)
            {
                SetDepthLightActive(station, false);
            }
        }

        captureStatus = string.Format(
            Tr("Captura concluída: {0} prévia VR da Scene View + {1} câmeras mono = {2} imagens Unity.", "Capture done: {0} Scene View VR preview + {1} mono cameras = {2} Unity images."),
            sceneViewCaptured ? 1 : 0,
            stations.Count,
            stations.Count + (sceneViewCaptured ? 1 : 0));
        Repaint();
    }

    private void CapturePimaxOnly()
    {
        var previousContext = new ShaderContextState();
        try
        {
            if (CapturePimaxPreview())
            {
                captureStatus = Tr("Captura concluída: os dois olhos foram renderizados em uma única imagem.", "Capture done: both eyes were rendered into a single image.");
            }
        }
        finally
        {
            previousContext.Restore();
        }
        Repaint();
    }

    private void CreateCameraStations()
    {
        for (var index = 0; index < stationCount; index++)
        {
            var station = new CameraStation
            {
                Index = index,
                Distance = firstDistance + index * intervalMeters
            };
            // O shader classifica a câmera por _ScreenParams.y: a textura precisa ter a altura simulada.
            var image = new RenderTexture(simulatedScreenHeight, simulatedScreenHeight, 24, RenderTextureFormat.ARGB32)
            {
                name = string.Format("ShaderView {0}m Context", station.Distance),
                hideFlags = HideFlags.HideAndDontSave,
                antiAliasing = 1
            };
            image.Create();
            station.Image = image;
            station.BackgroundCamera = CreateDistanceCamera(station, image, true);
            station.Camera = CreateDistanceCamera(station, image, false);
            UpdateDistanceCamera(station);
            station.DepthLight = CreateDepthLight(station);
            stations.Add(station);
        }
    }

    private void EnsureCameraStations()
    {
        if (stations.Count != stationCount
            || stations.Count == 0
            || stations[0].Image == null
            || stations[0].Camera == null
            || stations[0].BackgroundCamera == null
            || stations[0].Image.width != simulatedScreenHeight
            || stations[0].Image.height != simulatedScreenHeight)
        {
            DestroyCameraStations();
            CreateCameraStations();
            return;
        }

        for (var index = 0; index < stations.Count; index++)
        {
            stations[index].Index = index;
            stations[index].Distance = firstDistance + index * intervalMeters;
            UpdateDistanceCamera(stations[index]);
        }
    }

    private Camera CreateDistanceCamera(CameraStation station, RenderTexture target, bool backgroundCamera)
    {
        var role = backgroundCamera ? "background" : "shader";
        var cameraObject = new GameObject(
            string.Format("ShaderView hidden {0} camera {1}m", role, station.Distance))
        {
            hideFlags = HideFlags.HideAndDontSave
        };
        var camera = cameraObject.AddComponent<Camera>();
        camera.enabled = false;
        camera.targetTexture = target;
        camera.clearFlags = backgroundCamera ? CameraClearFlags.Skybox : CameraClearFlags.Depth;
        camera.fieldOfView = verticalFov;
        camera.aspect = 1f;
        camera.nearClipPlane = 0.03f;
        camera.farClipPlane = 300f;
        camera.depthTextureMode = backgroundCamera || station.Index == 0
            ? DepthTextureMode.None
            : DepthTextureMode.Depth;
        camera.allowHDR = true;
        camera.allowMSAA = false;
        camera.stereoTargetEye = StereoTargetEyeMask.None;
        camera.cullingMask = backgroundCamera ? ~0 : 0;
        return camera;
    }

    private void UpdateDistanceCamera(CameraStation station)
    {
        var camera = station.Camera;
        if (camera == null)
        {
            return;
        }
        if (station.BackgroundCamera == null)
        {
            throw new System.InvalidOperationException(
                "A distance station is missing its background capture camera.");
        }
        camera.fieldOfView = verticalFov;
        camera.aspect = 1f;
        camera.depthTextureMode = station.Index == 0
            ? DepthTextureMode.None
            : DepthTextureMode.Depth;
        var position = targetObject.transform.position + GetStationOffset(station);
        var rotation = Quaternion.LookRotation(-GetStationOffset(station).normalized, targetObject.transform.up);
        camera.transform.SetPositionAndRotation(position, rotation);
        station.BackgroundCamera.fieldOfView = verticalFov;
        station.BackgroundCamera.aspect = 1f;
        station.BackgroundCamera.transform.SetPositionAndRotation(position, rotation);
    }

    private Vector3 GetStationOffset(CameraStation station)
    {
        return GetStationOffset(station.Distance);
    }

    private Vector3 GetStationOffset(float distance)
    {
        var up = targetObject.transform.up;
        var forward = targetObject.transform.forward;
        var offset = (-forward * distance + up * cameraHeightOffset).normalized * distance;
        return Quaternion.AngleAxis(orbitAngleDegrees, up) * offset;
    }

    private bool CaptureSceneViewVr()
    {
        var sceneView = SceneView.lastActiveSceneView;
        sceneViewCamera = sceneView != null ? sceneView.camera : null;
        if (sceneViewCamera == null && Application.isBatchMode)
        {
            sceneViewCamera = Camera.main;
        }
        if (sceneViewCamera == null)
        {
            captureStatus = Tr("Não há uma Scene View ativa para a prévia VR.", "There is no active Scene View for the VR preview.");
            DestroyRenderTexture(leftSceneVrImage);
            DestroyRenderTexture(rightSceneVrImage);
            leftSceneVrImage = null;
            rightSceneVrImage = null;
            return false;
        }

        if (leftSceneVrCamera == null || rightSceneVrCamera == null)
        {
            leftSceneVrCamera = CreateSceneVrCamera("left");
            rightSceneVrCamera = CreateSceneVrCamera("right");
        }

        var outputHeight = simulatedScreenHeight;
        var aspect = VrAspectRatios[Mathf.Clamp(vrAspectIndex, 0, VrAspectRatios.Length - 1)];
        var outputWidth = Mathf.Max(1, Mathf.RoundToInt(outputHeight * aspect));
        if (leftSceneVrImage == null
            || rightSceneVrImage == null
            || leftSceneVrImage.width != outputWidth
            || leftSceneVrImage.height != outputHeight)
        {
            DestroyRenderTexture(leftSceneVrImage);
            DestroyRenderTexture(rightSceneVrImage);
            leftSceneVrImage = CreateSceneVrTexture(outputWidth, outputHeight, "Left");
            rightSceneVrImage = CreateSceneVrTexture(outputWidth, outputHeight, "Right");
        }

        RenderSceneViewEye(
            leftSceneVrCamera,
            leftSceneVrImage,
            -1f,
            new Rect(0f, 0f, 1f, 1f),
            verticalFov,
            0f,
            0f);
        RenderSceneViewEye(
            rightSceneVrCamera,
            rightSceneVrImage,
            1f,
            new Rect(0f, 0f, 1f, 1f),
            verticalFov,
            0f,
            0f);
        return true;
    }

    private bool CapturePimaxPreview()
    {
        var sceneView = SceneView.lastActiveSceneView;
        sceneViewCamera = sceneView != null ? sceneView.camera : null;
        if (sceneViewCamera == null && Application.isBatchMode)
        {
            sceneViewCamera = Camera.main;
        }
        if (sceneViewCamera == null)
        {
            captureStatus = Tr("Não há uma câmera de Scene View ativa para a prévia estéreo.", "There is no active Scene View camera for the stereo preview.");
            return false;
        }

        if (leftSceneVrCamera == null || rightSceneVrCamera == null)
        {
            leftSceneVrCamera = CreateSceneVrCamera("Pimax left");
            rightSceneVrCamera = CreateSceneVrCamera("Pimax right");
        }

        var eyeAspect = VrAspectRatios[Mathf.Clamp(pimaxAspectIndex, 0, VrAspectRatios.Length - 1)];
        var eyeWidth = Mathf.Max(1, Mathf.RoundToInt(simulatedScreenHeight * eyeAspect));
        if (pimaxStereoImage == null
            || pimaxStereoImage.width != eyeWidth * 2
            || pimaxStereoImage.height != simulatedScreenHeight)
        {
            DestroyRenderTexture(pimaxStereoImage);
            pimaxStereoImage = CreateSceneVrTexture(eyeWidth * 2, simulatedScreenHeight, "Pimax stereo side by side");
        }

        RenderSceneViewEye(
            leftSceneVrCamera,
            pimaxStereoImage,
            -1f,
            new Rect(0f, 0f, 0.5f, 1f),
            pimaxVerticalFov,
            -pimaxEyeCantDegrees,
            -pimaxProjectionAsymmetry);
        RenderSceneViewEye(
            rightSceneVrCamera,
            pimaxStereoImage,
            1f,
            new Rect(0.5f, 0f, 0.5f, 1f),
            pimaxVerticalFov,
            pimaxEyeCantDegrees,
            pimaxProjectionAsymmetry);
        return true;
    }

    private static Camera CreateSceneVrCamera(string eye)
    {
        var cameraObject = new GameObject("ShaderView hidden Scene View VR camera " + eye)
        {
            hideFlags = HideFlags.HideAndDontSave
        };
        var camera = cameraObject.AddComponent<Camera>();
        camera.enabled = false;
        return camera;
    }

    private static RenderTexture CreateSceneVrTexture(int width, int height, string eye)
    {
        var texture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32)
        {
            name = "ShaderView Scene View VR " + eye,
            hideFlags = HideFlags.HideAndDontSave,
            antiAliasing = 1
        };
        texture.Create();
        return texture;
    }

    private void RenderSceneViewEye(
        Camera eyeCamera,
        RenderTexture image,
        float eyeSide,
        Rect viewport,
        float fieldOfView,
        float eyeCantDegrees,
        float projectionAsymmetry)
    {
        eyeCamera.enabled = false;
        eyeCamera.targetTexture = image;
        eyeCamera.rect = viewport;
        eyeCamera.clearFlags = CameraClearFlags.Skybox;
        eyeCamera.fieldOfView = fieldOfView;
        eyeCamera.aspect = image.width * viewport.width / image.height;
        eyeCamera.orthographic = false;
        eyeCamera.nearClipPlane = 0.03f;
        eyeCamera.farClipPlane = 300f;
        eyeCamera.depthTextureMode = DepthTextureMode.Depth;
        eyeCamera.allowHDR = true;
        eyeCamera.allowMSAA = false;
        eyeCamera.cullingMask = ~0;
        eyeCamera.stereoTargetEye = StereoTargetEyeMask.None;

        var eyeOffset = eyeSide * eyeSeparationMeters * 0.5f;
        eyeCamera.transform.SetPositionAndRotation(
            sceneViewCamera.transform.position + sceneViewCamera.transform.right * eyeOffset,
            sceneViewCamera.transform.rotation * Quaternion.AngleAxis(eyeCantDegrees, Vector3.up));
        eyeCamera.projectionMatrix = CreateSceneViewEyeProjection(
            eyeCamera.aspect,
            eyeCamera.nearClipPlane,
            eyeCamera.farClipPlane,
            eyeOffset,
            fieldOfView,
            projectionAsymmetry,
            eyeSide);
        SetShaderContext(true, eyeSide > 0f ? 1f : 0f, eyeCamera, sceneViewCamera.transform.position);
        eyeCamera.Render();
    }

    private Matrix4x4 CreateSceneViewEyeProjection(
        float aspect,
        float near,
        float far,
        float eyeOffset,
        float fieldOfView,
        float projectionAsymmetry,
        float eyeSide)
    {
        var halfFrustumHeight = near * Mathf.Tan(fieldOfView * Mathf.Deg2Rad * 0.5f);
        var halfFrustumWidth = halfFrustumHeight * aspect;
        var convergence = Mathf.Max(convergenceDistanceMeters, near);
        var frustumShift = -eyeOffset * near / convergence
            + eyeSide * projectionAsymmetry * halfFrustumWidth;
        return Matrix4x4.Frustum(
            -halfFrustumWidth + frustumShift,
            halfFrustumWidth + frustumShift,
            -halfFrustumHeight,
            halfFrustumHeight,
            near,
            far);
    }

    private Light CreateDepthLight(CameraStation station)
    {
        var lightObject = new GameObject(
            string.Format("ShaderView hidden DepthLight {0}m", station.Distance))
        {
            hideFlags = HideFlags.HideAndDontSave
        };
        lightObject.transform.rotation = targetObject.transform.rotation;
        var light = lightObject.AddComponent<Light>();
        light.type = LightType.Directional;
        light.color = Color.black;
        light.intensity = 0.01f;
        light.shadows = LightShadows.Hard;
        light.lightmapBakeType = LightmapBakeType.Baked;
        light.cullingMask = ~0;
        light.enabled = false;
        lightObject.SetActive(false);
        return light;
    }

    private static void SetDepthLightActive(CameraStation station, bool active)
    {
        if (station.DepthLight == null)
        {
            return;
        }
        station.DepthLight.gameObject.SetActive(active);
        station.DepthLight.enabled = active;
    }

    private int FindUnusedLayer()
    {
        var usedLayers = new bool[32];
        foreach (var sceneObject in FindObjectsOfType<GameObject>(true))
        {
            if (sceneObject.scene.IsValid() && sceneObject.scene.isLoaded)
            {
                usedLayers[sceneObject.layer] = true;
            }
        }

        for (var layer = usedLayers.Length - 1; layer >= FirstUserLayer; layer--)
        {
            if (!usedLayers[layer])
            {
                return layer;
            }
        }

        throw new System.InvalidOperationException(
            "No unused user layer is available to isolate the shader object during camera capture.");
    }

    private List<SavedLayer> MoveTargetToLayer(int layer)
    {
        var savedLayers = new List<SavedLayer>();
        foreach (var childTransform in targetObject.GetComponentsInChildren<Transform>(true))
        {
            var target = childTransform.gameObject;
            savedLayers.Add(new SavedLayer
            {
                Object = target,
                Layer = target.layer
            });
            target.layer = layer;
        }
        return savedLayers;
    }

    private static void RestoreLayers(List<SavedLayer> savedLayers)
    {
        foreach (var savedLayer in savedLayers)
        {
            if (savedLayer.Object != null)
            {
                savedLayer.Object.layer = savedLayer.Layer;
            }
        }
    }

    private void DestroyCameraStations()
    {
        foreach (var station in stations)
        {
            DestroyRenderTexture(station.Image);
            DestroyHiddenObject(station.Camera != null ? station.Camera.gameObject : null);
            DestroyHiddenObject(station.BackgroundCamera != null ? station.BackgroundCamera.gameObject : null);
            DestroyHiddenObject(station.DepthLight != null ? station.DepthLight.gameObject : null);
        }
        stations.Clear();
    }

    private void DestroySceneVrCameras()
    {
        DestroyRenderTexture(leftSceneVrImage);
        DestroyRenderTexture(rightSceneVrImage);
        DestroyRenderTexture(pimaxStereoImage);
        leftSceneVrImage = null;
        rightSceneVrImage = null;
        pimaxStereoImage = null;
        DestroyHiddenObject(leftSceneVrCamera != null ? leftSceneVrCamera.gameObject : null);
        DestroyHiddenObject(rightSceneVrCamera != null ? rightSceneVrCamera.gameObject : null);
        leftSceneVrCamera = null;
        rightSceneVrCamera = null;
        sceneViewCamera = null;
    }

    private static void DestroyRenderTexture(RenderTexture texture)
    {
        if (texture == null)
        {
            return;
        }
        texture.Release();
        DestroyImmediate(texture);
    }

    private static void DestroyHiddenObject(Object value)
    {
        if (value != null)
        {
            DestroyImmediate(value);
        }
    }

    private void OpenPreviewScene()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            EditorUtility.DisplayDialog(
                "ShaderView",
                Tr("Saia do Play Mode antes de abrir novamente a cena de teste.", "Exit Play Mode before opening the test scene again."),
                "OK");
            return;
        }

        if (string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(SceneAsset, AssetPathToGUIDOptions.OnlyExistingAssets)))
        {
            captureStatus = Tr("A cena de teste não está neste projeto. Selecione o objeto do shader.", "The test scene isn't in this project. Select the shader object.");
            return;
        }

        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
        {
            return;
        }
        EditorSceneManager.OpenScene(SceneAsset, OpenSceneMode.Single);
        FindDefaultAssets();
        captureRequested = true;
    }

    private void DrawCameraPositions(SceneView sceneView)
    {
        if (targetObject == null)
        {
            return;
        }

        var origin = targetObject.transform.position;
        for (var index = 0; index < stationCount; index++)
        {
            var distance = firstDistance + index * intervalMeters;
            var position = origin + GetStationOffset(distance);
            Handles.color = index == 0 ? Color.yellow : new Color(1f, 0.35f, 0.12f);
            Handles.SphereHandleCap(0, position, Quaternion.identity, 0.12f, EventType.Repaint);
            Handles.Label(position, string.Format("{0:0.##} m", distance));
        }
    }
}
