using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public sealed class ShaderViewVrDebuggerWindow : EditorWindow
{
    private const string ProjectVersion = "0.5.0";
    private const int FirstUserLayer = 8;
    private const string SceneAsset = "Assets/ShaderView/ShaderViewPreview.unity";
    private const string MaterialAsset = "Assets/ShaderView/Materials/Center 1.mat";
    // O caminho do shader muda entre projetos; o GUID do .meta, não.
    private const string ShaderGuid = "0510857c96ead304b956c1a90edfcfe8";
    private const string TestObjectName = "Shader under test / ScreenSpace Ubershader";
    private static readonly string[] VrAspectLabels = { "1:1", "4:3", "16:9", "21:9" };
    private static readonly float[] VrAspectRatios = { 1f, 4f / 3f, 16f / 9f, 21f / 9f };
    private static readonly string[] TabLabels = { "Debugger", "IF pimax" };

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
    private string captureStatus = "Configure as câmeras e clique em Capturar todas.";
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

        public ShaderContextState()
        {
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
        var window = GetWindow<ShaderViewVrDebuggerWindow>($"VR Camera Debugger v{ProjectVersion}");
        window.minSize = new Vector2(640f, 400f);
        window.FindDefaultAssets();
        if (window.targetObject == null && !EditorApplication.isPlayingOrWillChangePlaymode)
        {
            window.OpenPreviewScene();
        }
        window.Show();
    }

    private void OnEnable()
    {
        FindDefaultAssets();
        captureRequested = CanCapture();
        SceneView.duringSceneGui += DrawCameraPositions;
        SceneView.duringSceneGui += HandleSceneViewRepaint;
        EditorApplication.update += HandleEditorUpdate;
    }

    private void OnDisable()
    {
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
                string.Format("{0} / {1} capturas{2}", stations.Count, stationCount, autoRefresh ? " · contínua" : string.Empty),
                complete ? DoskerEditorUi.Green : DoskerEditorUi.Yellow,
                16f);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(DoskerEditorUi.Content(" Abrir cena de teste", "SceneAsset Icon"), EditorStyles.toolbarButton))
            {
                OpenPreviewScene();
            }
            if (GUILayout.Button(DoskerEditorUi.Content(" Localizar objeto do shader", "Search Icon"), EditorStyles.toolbarButton))
            {
                FindDefaultAssets();
                if (targetObject != null)
                {
                    Selection.activeGameObject = targetObject;
                    EditorGUIUtility.PingObject(targetObject);
                }
            }
        }
    }

    private void DrawStatus()
    {
        using (var card = new DoskerEditorUi.CardScope(
            "Debugger.About",
            "Sobre as capturas",
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
                "A prévia VR única acompanha a câmera ativa da Scene View. As capturas adicionais são mono e ficam nas distâncias configuradas, " +
                "todas usando o mesmo perfil de contexto acima. A referência renderiza o mundo sem o objeto-alvo e depois captura só o alvo " +
                "com a profundidade do mundo isolada; as demais usam o mesmo processo com DepthLight ativa, " +
                "intensidade 0,01, sombras Hard e modo Baked.",
                MessageType.Info);

            EditorGUILayout.LabelField(
                string.Format(
                    "Distâncias: {0} · início: {1:0.##} m · intervalo: {2:0.##} m · VR Scene View: 1 · sem depth/DepthLight: 1 · com DepthLight: {3}",
                    stationCount,
                    firstDistance,
                    intervalMeters,
                    depthLightCount),
                EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.HelpBox(
                "A DepthLight não cria mapa de profundidade: a depth texture vem da geometria. A simulação controla as ramificações do shader, " +
                "mas não substitui o runtime XR/VRChat.",
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
            autoRefresh = EditorGUILayout.ToggleLeft("Atualização contínua", autoRefresh);
            if (autoRefresh)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField("FPS da captura", GUILayout.Width(100f));
                    refreshRate = EditorGUILayout.IntPopup(
                        refreshRate,
                        new[] { "Nativo (Scene View)", "2", "5", "10", "15", "30", "60" },
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
            using (var card = new DoskerEditorUi.CardScope("Debugger.Cameras", "Câmeras e distâncias", "Camera Icon", DoskerEditorUi.Blue))
            {
                if (card.Expanded)
                {
                    stationCount = EditorGUILayout.IntSlider("Quantidade de distâncias", stationCount, 1, 24);
                    firstDistance = EditorGUILayout.Slider("Primeira distância (m)", firstDistance, 0.1f, 20f);
                    intervalMeters = EditorGUILayout.Slider("Intervalo entre câmeras (m)", intervalMeters, 0.1f, 5f);
                    cameraHeightOffset = EditorGUILayout.Slider("Altura relativa (m)", cameraHeightOffset, -2f, 3f);
                    orbitAngleDegrees = EditorGUILayout.Slider("Rotação em volta do alvo (°)", orbitAngleDegrees, 0f, 360f);
                    verticalFov = EditorGUILayout.Slider("FOV das câmeras (°)", verticalFov, 30f, 120f);
                    vrAspectIndex = EditorGUILayout.Popup("Proporção da prévia VR", vrAspectIndex, VrAspectLabels);
                    eyeSeparationMeters = EditorGUILayout.Slider("IPD estéreo (m)", eyeSeparationMeters, 0.04f, 0.08f);
                    convergenceDistanceMeters = EditorGUILayout.Slider("Convergência estéreo (m)", convergenceDistanceMeters, 0.5f, 20f);
                }
            }

            using (var card = new DoskerEditorUi.CardScope("Debugger.Context", "Contexto simulado", "SettingsIcon", DoskerEditorUi.Purple))
            {
                if (card.Expanded)
                {
                    simulatedVr = EditorGUILayout.Toggle("É VR", simulatedVr);
                    using (new EditorGUI.DisabledScope(!simulatedVr))
                    {
                        simulatedEyeIndex = EditorGUILayout.Popup("Olho simulado", simulatedEyeIndex, new[] { "Esquerdo", "Direito" });
                    }
                    simulatedMirror = EditorGUILayout.Toggle("É espelho", simulatedMirror);
                    simulatedPersonalMirror = EditorGUILayout.Toggle("É espelho pessoal", simulatedPersonalMirror);
                    simulatedCameraMode = EditorGUILayout.Popup(
                        "Tipo de câmera",
                        simulatedCameraMode,
                        new[] { "Regular", "Câmera VR de mão", "Câmera desktop de mão", "Foto / screenshot" });
                    simulatedCameraUi = EditorGUILayout.Toggle("UI visível na câmera", simulatedCameraUi);
                    DrawContextWarnings();
                }
            }

            using (var card = new DoskerEditorUi.CardScope("Debugger.Output", "Resolução e prévia", "RawImage Icon", DoskerEditorUi.Green))
            {
                if (card.Expanded)
                {
                    simulatedScreenHeight = EditorGUILayout.IntPopup(
                        "Resolução (altura simulada)",
                        simulatedScreenHeight,
                        new[] { "720 (prévia da câmera)", "1080", "1440", "2160 (4K)" },
                        new[] { 720, 1080, 1440, 2160 });
                    monoColumns = EditorGUILayout.IntSlider("Colunas mono por linha", monoColumns, 1, 8);
                    previewImageHeight = EditorGUILayout.Slider("Altura das prévias (px)", previewImageHeight, 100f, 800f);
                }
            }
            if (EditorGUI.EndChangeCheck())
            {
                captureRequested = true;
            }

            DrawStatus();
            EditorGUILayout.EndScrollView();
            DrawCaptureFooter("Capturar todas");
        }
    }

    private void DrawContextWarnings()
    {
        if (material != null && material.HasProperty(OverlayVisibilityProperty) && !ShaderHasVrSimulation(material.shader))
        {
            EditorGUILayout.HelpBox(
                "Este shader é anterior à simulação de VR: sem " + SimulatedVrVariable + ", a visão principal continua igual à de desktop. " +
                "Atualize o shader para testar os filtros por VR e por olho fora de espelho.",
                MessageType.Warning);
        }
        if (!simulatedMirror && !ShouldSimulateVr(true))
        {
            EditorGUILayout.HelpBox(
                "A simulação de VR fica desligada em espelho pessoal, câmera de mão e foto: nada confirma que esses renders são stereo.",
                MessageType.Info);
        }
        if (simulatedCameraMode == 1 && !simulatedMirror)
        {
            EditorGUILayout.HelpBox(
                "Câmera VR de mão segue o filtro Camera_Desktop: o filtro Camera_VR fica dentro de isVR(), que é falso em câmera não stereo.",
                MessageType.Info);
        }
        if (material != null
            && material.HasProperty(OverlayVisibilityProperty)
            && material.GetFloat(OverlayVisibilityProperty) < 0.5f)
        {
            EditorGUILayout.HelpBox(
                "O material está com Overlay/Visibility desligado: os filtros por contexto (espelho, câmera, olho) não são avaliados, " +
                "então mudar o contexto não altera o overlay.",
                MessageType.Warning);
        }
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
        using (var card = new DoskerEditorUi.CardScope("Debugger.Target", "Alvo", "GameObject Icon", DoskerEditorUi.Cyan))
        {
            if (!card.Expanded)
            {
                return;
            }

            var nextTarget = (GameObject)EditorGUILayout.ObjectField(
                "Objeto do shader",
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
                "Material no Inspector (opcional)",
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

            if (material == null)
            {
                EditorGUILayout.HelpBox(
                    "Sem material para abrir no Inspector. As capturas continuam usando os materiais do objeto.",
                    MessageType.None);
            }
            else if (!IsUsableMaterial(material))
            {
                EditorGUILayout.HelpBox(
                    "O material do Inspector está inválido. As capturas não são bloqueadas; será usado o primeiro material do objeto, se disponível.",
                    MessageType.Warning);
            }

            EditorGUILayout.HelpBox(
                "O inspector original edita as propriedades do material e do shader; ele não controla as câmeras. " +
                "Abra-o no Inspector normal para preservar espaço para as imagens.",
                MessageType.None);
            DrawSelectMaterialButton();
        }
    }

    private void DrawSelectMaterialButton()
    {
        // Abrir o material não é uma alteração de parâmetro: não dispara nova captura.
        var previousChanged = GUI.changed;
        using (new EditorGUI.DisabledScope(material == null))
        {
            if (GUILayout.Button(DoskerEditorUi.Content(" Selecionar material no Inspector do Unity", "Material Icon")))
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
                EditorGUILayout.LabelField("Prévia VR estéreo · câmera da Scene View", EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                DoskerEditorUi.Badge(string.Format("IPD {0:0} mm", eyeSeparationMeters * 1000f), DoskerEditorUi.Blue);
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                DrawEyeImage("OLHO ESQUERDO · Scene View", leftSceneVrImage, previewImageHeight);
                DrawEyeImage("OLHO DIREITO · Scene View", rightSceneVrImage, previewImageHeight);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Capturas mono por distância", EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                DoskerEditorUi.Badge(string.Format("{0} / {1}", stations.Count, stationCount), DoskerEditorUi.Blue);
            }

            if (stations.Count == 0)
            {
                EditorGUILayout.HelpBox(captureStatus, MessageType.None);
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
                    DoskerEditorUi.Badge("sem depth", DoskerEditorUi.Yellow);
                }
            }
            DrawEyeImage("Contexto selecionado · mono", station.Image, Mathf.Min(previewImageHeight * 0.6f, 220f));
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
                "Renderiza os dois olhos em uma única imagem lado a lado, com FOV, inclinação e projeção assimétrica ajustáveis. " +
                "É uma aproximação visual: não acessa o perfil óptico nem as matrizes reais do SteamVR/Pimax.",
                MessageType.Warning);
            EditorGUILayout.EndScrollView();
            DrawCaptureFooter("Capturar IF pimax");
        }
    }

    private void DrawPimaxImagePanel()
    {
        using (new EditorGUILayout.VerticalScope(GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true)))
        {
            pimaxImageScroll = EditorGUILayout.BeginScrollView(pimaxImageScroll, GUILayout.ExpandHeight(true));
            if (pimaxStereoImage != null && pimaxStereoImage.IsCreated())
            {
                DrawEyeImage("Resultado estereoscópico · olho esquerdo | olho direito", pimaxStereoImage, previewImageHeight);
            }
            else
            {
                EditorGUILayout.HelpBox("Configure o perfil e clique em Capturar IF pimax para gerar a imagem.", MessageType.None);
            }
            EditorGUILayout.EndScrollView();
        }
    }

    private void DrawPimaxSettings()
    {
        EditorGUI.BeginChangeCheck();
        using (var card = new DoskerEditorUi.CardScope("Pimax.Target", "Alvo", "GameObject Icon", DoskerEditorUi.Cyan))
        {
            if (card.Expanded)
            {
                var nextTarget = (GameObject)EditorGUILayout.ObjectField(
                    "Objeto do shader",
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
                    "Material no Inspector (opcional)",
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

        using (var card = new DoskerEditorUi.CardScope("Pimax.Projection", "Projeção estereoscópica", "Camera Icon", DoskerEditorUi.Purple))
        {
            if (card.Expanded)
            {
                pimaxVerticalFov = EditorGUILayout.Slider("FOV vertical por olho (°)", pimaxVerticalFov, 60f, 140f);
                pimaxAspectIndex = EditorGUILayout.Popup("Proporção de cada olho", pimaxAspectIndex, VrAspectLabels);
                eyeSeparationMeters = EditorGUILayout.Slider("IPD (m)", eyeSeparationMeters, 0.04f, 0.08f);
                convergenceDistanceMeters = EditorGUILayout.Slider(
                    "Convergência estéreo (m)",
                    convergenceDistanceMeters,
                    0.5f,
                    20f);
                pimaxEyeCantDegrees = EditorGUILayout.Slider("Inclinação das lentes (°)", pimaxEyeCantDegrees, 0f, 15f);
                pimaxProjectionAsymmetry = EditorGUILayout.Slider(
                    "Assimetria horizontal por olho",
                    pimaxProjectionAsymmetry,
                    0f,
                    0.2f);
            }
        }
        if (EditorGUI.EndChangeCheck())
        {
            captureRequested = true;
        }

        using (var card = new DoskerEditorUi.CardScope("Pimax.Output", "Prévia", "RawImage Icon", DoskerEditorUi.Green))
        {
            if (card.Expanded)
            {
                previewImageHeight = EditorGUILayout.Slider("Altura da prévia (px)", previewImageHeight, 100f, 800f);
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
            captureStatus = "Selecione o objeto do shader antes de capturar.";
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
    }

    private void CaptureAllStations()
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
            "Captura concluída: {0} prévia VR da Scene View + {1} câmeras mono = {2} imagens Unity.",
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
                captureStatus = "Captura concluída: os dois olhos IF pimax foram renderizados em uma única imagem.";
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
            captureStatus = "Não há uma Scene View ativa para a prévia VR.";
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
            captureStatus = "Não há uma câmera de Scene View ativa para a prévia IF pimax.";
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
                "Saia do Play Mode antes de abrir novamente a cena de teste.",
                "OK");
            return;
        }

        if (string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(SceneAsset, AssetPathToGUIDOptions.OnlyExistingAssets)))
        {
            captureStatus = "A cena de teste não está neste projeto. Selecione o objeto do shader.";
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

    public static void RunAutomatedValidation()
    {
        var shader = AssetDatabase.LoadAssetAtPath<Shader>(AssetDatabase.GUIDToAssetPath(ShaderGuid));
        var previewMaterial = AssetDatabase.LoadAssetAtPath<Material>(MaterialAsset);
        if (shader == null || previewMaterial == null)
        {
            throw new System.InvalidOperationException("The preview shader or material was not imported.");
        }
        if (ShaderUtil.ShaderHasError(shader) || !shader.isSupported)
        {
            throw new System.InvalidOperationException("The preview shader has compile errors or is unsupported.");
        }
        AssertShaderSupportsDebugger(shader);

        previewMaterial.shader = shader;
        var currentScene = SceneManager.GetActiveScene();
        if (currentScene.path != SceneAsset)
        {
            currentScene = EditorSceneManager.OpenScene(SceneAsset, OpenSceneMode.Single);
        }
        var target = GameObject.Find(TestObjectName);
        if (target == null)
        {
            throw new System.InvalidOperationException("The shader test object is missing from the preview scene.");
        }

        var targetMaterial = GetFirstTargetMaterial(target);
        var debugger = CreateInstance<ShaderViewVrDebuggerWindow>();
        if (!debugger.autoRefresh || debugger.refreshRate != 0)
        {
            throw new System.InvalidOperationException(
                "Automatic capture should default to the native Scene View refresh rate.");
        }
        debugger.targetObject = target;
        debugger.material = null;
        debugger.ResolveTargetMaterial();
        if (targetMaterial != null && debugger.material != targetMaterial)
        {
            throw new System.InvalidOperationException(
                "An unspecified Inspector material should resolve to the target object's first renderer material.");
        }
        if (!debugger.CanCapture())
        {
            throw new System.InvalidOperationException("Camera captures must not require a separately selected Inspector material.");
        }
        debugger.material = previewMaterial;
        debugger.materialManuallySelected = true;
        debugger.ResolveTargetMaterial();
        if (debugger.material != previewMaterial)
        {
            throw new System.InvalidOperationException("A valid manually selected Inspector material should be preserved.");
        }
        debugger.stationCount = 10;
        debugger.firstDistance = 1f;
        debugger.intervalMeters = 1f;
        debugger.simulatedScreenHeight = 720;
        debugger.CreateCameraStations();
        var orbitTestStation = debugger.stations[0];
        var initialOrbitPosition = orbitTestStation.Camera.transform.position;
        debugger.orbitAngleDegrees = 90f;
        debugger.UpdateDistanceCamera(orbitTestStation);
        var quarterTurnPosition = orbitTestStation.Camera.transform.position;
        debugger.orbitAngleDegrees = 360f;
        debugger.UpdateDistanceCamera(orbitTestStation);
        var fullTurnPosition = orbitTestStation.Camera.transform.position;
        debugger.orbitAngleDegrees = 0f;
        debugger.UpdateDistanceCamera(orbitTestStation);
        if (Vector3.Distance(initialOrbitPosition, quarterTurnPosition) < 0.1f
            || Vector3.Distance(initialOrbitPosition, fullTurnPosition) > 0.001f)
        {
            throw new System.InvalidOperationException(
                "Camera orbit rotation must move stations and return to their original positions at 360 degrees.");
        }
        debugger.CaptureAutomatedFrames();
        debugger.ValidateVrSimulation(shader);
        debugger.selectedTab = 1;
        if (!debugger.CapturePimaxPreview())
        {
            throw new System.InvalidOperationException("A Scene View camera is required to validate the IF pimax preview.");
        }
        AssertRenderedTexture(debugger.pimaxStereoImage, 0f, "IF pimax stereo");
        var expectedPimaxEyeWidth = Mathf.RoundToInt(
            debugger.simulatedScreenHeight * VrAspectRatios[debugger.pimaxAspectIndex]);
        if (debugger.pimaxStereoImage.width != expectedPimaxEyeWidth * 2
            || CountSideBySidePixelDifferences(debugger.pimaxStereoImage) == 0)
        {
            throw new System.InvalidOperationException(
                "The IF pimax preview must render distinguishable left and right eyes into a single side-by-side image.");
        }
        debugger.DestroyCameraStations();
        debugger.DestroySceneVrCameras();
        DestroyImmediate(debugger);

        Debug.Log("SHADERVIEW_VALIDATION_OK: distance labels are concise; IF pimax renders distinguishable stereo eyes into one image with per-eye projection; automatic capture defaults on; orbit stations rotate through 90 and return at 360 degrees; simulated VR eye filters, head center and context exclusions work; Scene View VR eyes and ten mono stations render; reference depth is isolated and DepthLight settings are preserved.");
        EditorApplication.Exit(0);
    }

    public static void RunMaterialResolutionValidation()
    {
        var shader = Shader.Find("Standard");
        if (shader == null)
        {
            throw new System.InvalidOperationException("Unity's Standard shader is required for material resolution validation.");
        }

        var target = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        var renderer = target.GetComponent<Renderer>();
        var targetMaterial = new Material(shader) { name = "ShaderView target material validation" };
        var manualMaterial = new Material(shader) { name = "ShaderView manual material validation" };
        var debugger = CreateInstance<ShaderViewVrDebuggerWindow>();
        try
        {
            renderer.sharedMaterial = targetMaterial;
            debugger.targetObject = target;
            debugger.material = null;
            debugger.ResolveTargetMaterial();
            if (debugger.material != targetMaterial)
            {
                throw new System.InvalidOperationException(
                    "The Inspector material did not default to the first material on the selected object's renderer.");
            }

            debugger.material = manualMaterial;
            debugger.ResolveTargetMaterial();
            if (debugger.material != targetMaterial)
            {
                throw new System.InvalidOperationException(
                    "An unpinned Inspector material should follow the first material on the selected object.");
            }

            debugger.material = manualMaterial;
            debugger.materialManuallySelected = true;
            debugger.ResolveTargetMaterial();
            if (debugger.material != manualMaterial)
            {
                throw new System.InvalidOperationException("A valid manual Inspector material was not preserved.");
            }

            debugger.material = null;
            debugger.materialManuallySelected = false;
            debugger.ResolveTargetMaterial();
            if (debugger.material != targetMaterial || !debugger.CanCapture())
            {
                throw new System.InvalidOperationException(
                    "Capturing must fall back to the selected object's material without requiring an Inspector selection.");
            }
        }
        finally
        {
            DestroyImmediate(debugger);
            DestroyImmediate(target);
            DestroyImmediate(targetMaterial);
            DestroyImmediate(manualMaterial);
        }

        Debug.Log("SHADERVIEW_MATERIAL_RESOLUTION_OK: renderer material fallback works, valid manual overrides are preserved, and capture has no separate material requirement.");
        EditorApplication.Exit(0);
    }

    private static void AssertShaderSupportsDebugger(Shader shader)
    {
        var assetPath = AssetDatabase.GetAssetPath(shader);
        if (!assetPath.StartsWith("Assets/", System.StringComparison.Ordinal))
        {
            throw new System.InvalidOperationException("The debugger shader must be stored under the Unity project's Assets folder.");
        }
        var importedPath = Path.Combine(Application.dataPath, assetPath.Substring("Assets/".Length));
        var importedSource = File.ReadAllText(importedPath);
        if (importedSource.Split(new[] { "GrabPass {" }, System.StringSplitOptions.None).Length - 1 != 1)
        {
            throw new System.InvalidOperationException(
                "The Unity shader must retain its GrabPass declaration.");
        }
        if (!importedSource.Contains(SimulatedVrVariable))
        {
            throw new System.InvalidOperationException(
                "The Unity shader predates the VR simulation variables.");
        }
    }

    private void ValidateVrSimulation(Shader shader)
    {
        const int size = 256;
        var previousContext = new ShaderContextState();
        var previousAsyncCompilation = ShaderUtil.allowAsyncCompilation;
        var camera = CreateSceneVrCamera("simulation test");
        var baseline = CreateSceneVrTexture(size, size, "Simulation baseline");
        var result = CreateSceneVrTexture(size, size, "Simulation result");
        var effectObject = new GameObject("ShaderView simulation test effect") { hideFlags = HideFlags.HideAndDontSave };
        // O shader lê os vértices em espaço de visão: a malha cobre a tela em qualquer pose da câmera.
        var mesh = new Mesh
        {
            hideFlags = HideFlags.HideAndDontSave,
            vertices = new[]
            {
                new Vector3(-4f, -4f, -1f),
                new Vector3(4f, -4f, -1f),
                new Vector3(4f, 4f, -1f),
                new Vector3(-4f, 4f, -1f)
            },
            triangles = new[] { 0, 1, 2, 0, 2, 3 },
            bounds = new Bounds(Vector3.zero, Vector3.one * 1000f)
        };
        var effectMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave, renderQueue = 4000 };
        effectMaterial.SetFloat("BossBar", 1f);
        effectMaterial.SetFloat(OverlayVisibilityProperty, 1f);
        effectMaterial.SetFloat("_ZTest", 8f);
        effectMaterial.SetFloat("_CullMode", 0f);
        effectMaterial.SetFloat("_MinRange", 5f);
        effectMaterial.SetFloat("_MaxRange", 10f);
        effectObject.AddComponent<MeshFilter>().sharedMesh = mesh;
        var effectRenderer = effectObject.AddComponent<MeshRenderer>();
        effectRenderer.sharedMaterial = effectMaterial;
        try
        {
            ShaderUtil.allowAsyncCompilation = false;
            effectObject.transform.position = targetObject.transform.position;
            camera.clearFlags = CameraClearFlags.Skybox;
            camera.fieldOfView = 60f;
            camera.aspect = 1f;
            camera.nearClipPlane = 0.03f;
            camera.farClipPlane = 300f;
            camera.depthTextureMode = DepthTextureMode.Depth;
            camera.stereoTargetEye = StereoTargetEyeMask.None;
            camera.transform.SetPositionAndRotation(
                effectObject.transform.position - Vector3.forward * 3f,
                Quaternion.LookRotation(Vector3.forward));
            var head = camera.transform.position;

            int CountEffectPixels(float eyeIndex, Vector3 headCenter)
            {
                SetShaderContext(true, eyeIndex, camera, headCenter);
                // A base usa o mesmo contexto: outros objetos com o shader também reagem a ele.
                effectRenderer.enabled = false;
                camera.targetTexture = baseline;
                camera.Render();
                effectRenderer.enabled = true;
                camera.targetTexture = result;
                camera.Render();
                return CountPixelDifferences(baseline, result);
            }

            effectMaterial.SetFloat(OverlayVisibilityProperty + "_RightEye", 0f);
            var hiddenPixels = CountEffectPixels(1f, head);
            var visiblePixels = CountEffectPixels(0f, head);
            if (hiddenPixels != 0 || visiblePixels == 0)
            {
                throw new System.InvalidOperationException(string.Format(
                    "RightEye off must hide only the simulated right eye (right {0} px, left {1} px).", hiddenPixels, visiblePixels));
            }
            effectMaterial.SetFloat(OverlayVisibilityProperty + "_RightEye", 1f);
            effectMaterial.SetFloat(OverlayVisibilityProperty + "_LeftEye", 0f);
            hiddenPixels = CountEffectPixels(0f, head);
            visiblePixels = CountEffectPixels(1f, head);
            if (hiddenPixels != 0 || visiblePixels == 0)
            {
                throw new System.InvalidOperationException(string.Format(
                    "LeftEye off must hide only the simulated left eye (left {0} px, right {1} px).", hiddenPixels, visiblePixels));
            }
            effectMaterial.SetFloat(OverlayVisibilityProperty + "_LeftEye", 1f);
            hiddenPixels = CountEffectPixels(0f, head + Vector3.up * 100f);
            if (hiddenPixels != 0)
            {
                throw new System.InvalidOperationException(string.Format(
                    "A head center beyond _MaxRange must cut the simulated effect ({0} px).", hiddenPixels));
            }

            simulatedMirror = true;
            SetShaderContext(true, 1f, camera, head);
            var offInMirror = Shader.GetGlobalFloat(SimulatedVrId) == 0f;
            simulatedMirror = false;
            simulatedPersonalMirror = true;
            SetShaderContext(true, 1f, camera, head);
            var offInPersonalMirror = Shader.GetGlobalFloat(SimulatedVrId) == 0f;
            simulatedPersonalMirror = false;
            simulatedCameraMode = 1;
            SetShaderContext(true, 1f, camera, head);
            var offInHandCamera = Shader.GetGlobalFloat(SimulatedVrId) == 0f;
            simulatedCameraMode = 3;
            SetShaderContext(true, 1f, camera, head);
            var offInPhoto = Shader.GetGlobalFloat(SimulatedVrId) == 0f;
            simulatedCameraMode = 0;
            if (!offInMirror || !offInPersonalMirror || !offInHandCamera || !offInPhoto)
            {
                throw new System.InvalidOperationException(
                    "The VR simulation must stay off in mirrors, personal mirrors, hand cameras and photos.");
            }

            if (!CaptureSceneViewVr()
                || Shader.GetGlobalFloat(SimulatedVrId) != 1f
                || Shader.GetGlobalFloat(SimulatedEyeIndexId) != 1f
                || Vector3.Distance(Shader.GetGlobalVector(SimulatedCameraCenterId), sceneViewCamera.transform.position) > 0.001f)
            {
                throw new System.InvalidOperationException(
                    "The stereo preview must simulate each eye around the Scene View head position.");
            }
        }
        finally
        {
            ShaderUtil.allowAsyncCompilation = previousAsyncCompilation;
            previousContext.Restore();
            DestroyHiddenObject(camera.gameObject);
            DestroyHiddenObject(effectObject);
            DestroyHiddenObject(mesh);
            DestroyHiddenObject(effectMaterial);
            DestroyRenderTexture(baseline);
            DestroyRenderTexture(result);
        }
        if (!previousContext.IsCurrent())
        {
            throw new System.InvalidOperationException("The VR simulation globals were not restored after capture.");
        }
    }

    private void CaptureAutomatedFrames()
    {
        var previousContext = new ShaderContextState();
        List<SavedLayer> savedLayers = null;
        try
        {
            if (!CaptureSceneViewVr())
            {
                throw new System.InvalidOperationException("A Scene View camera is required to validate the VR preview.");
            }
            AssertRenderedTexture(leftSceneVrImage, 0f, "left Scene View VR");
            AssertRenderedTexture(rightSceneVrImage, 0f, "right Scene View VR");
            if (CountPixelDifferences(leftSceneVrImage, rightSceneVrImage) == 0)
            {
                throw new System.InvalidOperationException("The Scene View VR preview did not produce distinct left and right eye images.");
            }
            var sceneViewPosition = sceneViewCamera.transform.position;
            var averageEyePosition = (leftSceneVrCamera.transform.position + rightSceneVrCamera.transform.position) * 0.5f;
            if (Vector3.Distance(sceneViewPosition, averageEyePosition) > 0.001f
                || Mathf.Abs(
                    Vector3.Distance(leftSceneVrCamera.transform.position, rightSceneVrCamera.transform.position)
                    - eyeSeparationMeters) > 0.001f
                || Quaternion.Angle(leftSceneVrCamera.transform.rotation, sceneViewCamera.transform.rotation) > 0.001f
                || Quaternion.Angle(rightSceneVrCamera.transform.rotation, sceneViewCamera.transform.rotation) > 0.001f
                || !Mathf.Approximately(leftSceneVrCamera.fieldOfView, verticalFov)
                || !Mathf.Approximately(rightSceneVrCamera.fieldOfView, verticalFov)
                || !Mathf.Approximately(
                    leftSceneVrCamera.aspect,
                    leftSceneVrImage.width / (float)leftSceneVrImage.height)
                || !Mathf.Approximately(
                    rightSceneVrCamera.aspect,
                    rightSceneVrImage.width / (float)rightSceneVrImage.height))
            {
                throw new System.InvalidOperationException(
                    "The stereo preview must track only the Scene View pose while using the debugger FOV, aspect, and IPD.");
            }

            var targetLayer = FindUnusedLayer();
            savedLayers = MoveTargetToLayer(targetLayer);
            var targetLayerMask = 1 << targetLayer;
            if (targetObject.layer != targetLayer)
            {
                throw new System.InvalidOperationException(
                    "The shader target was not moved to the temporary isolated render layer.");
            }
            foreach (var station in stations)
            {
                SetDepthLightActive(station, station.Index > 0);
                SetShaderContext(simulatedVr, simulatedEyeIndex, station.Camera, station.Camera.transform.position);
                var expectedShaderMask = station.Index == 0 ? targetLayerMask : ~0;
                if (station.Index == 0)
                {
                    station.BackgroundCamera.cullingMask = ~targetLayerMask;
                    station.BackgroundCamera.clearFlags = CameraClearFlags.Skybox;
                }
                station.Camera.cullingMask = expectedShaderMask;
                var depthTextureMode = station.Camera.depthTextureMode;
                var hasDepthTexture = (depthTextureMode & DepthTextureMode.Depth) != 0;
                if (station.Index == 0 && depthTextureMode != DepthTextureMode.None)
                {
                    throw new System.InvalidOperationException(
                        "The no-depth reference camera must not request any camera depth texture mode.");
                }
                if (station.Index > 0 && !hasDepthTexture)
                {
                    throw new System.InvalidOperationException("A distance camera is missing the depth texture required by the shader.");
                }
                if (station.Camera.cullingMask != expectedShaderMask
                    || (station.Index == 0 && (station.BackgroundCamera.cullingMask & targetLayerMask) != 0))
                {
                    throw new System.InvalidOperationException(
                        "The reference and depth-enabled cameras are not using their expected render masks.");
                }
                if (station.DepthLight.color != Color.black
                    || !Mathf.Approximately(station.DepthLight.intensity, 0.01f)
                    || station.DepthLight.shadows != LightShadows.Hard
                    || station.DepthLight.lightmapBakeType != LightmapBakeType.Baked)
                {
                    throw new System.InvalidOperationException("DepthLight must be black, intensity 0.01, Hard shadows, and Baked.");
                }
                if (station.Index == 0)
                {
                    station.BackgroundCamera.Render();
                    station.Camera.clearFlags = CameraClearFlags.Depth;
                }
                else
                {
                    station.Camera.clearFlags = CameraClearFlags.Skybox;
                }
                station.Camera.Render();
                AssertRenderedTexture(station.Image, station.Distance, "mono");
                if (station.Index == 0 && station.DepthLight.gameObject.activeSelf)
                {
                    throw new System.InvalidOperationException("Reference camera unexpectedly activated the DepthLight GameObject.");
                }
                if (station.Index > 0 && !station.DepthLight.gameObject.activeSelf)
                {
                    throw new System.InvalidOperationException("A non-reference camera did not activate its DepthLight GameObject.");
                }
                SetDepthLightActive(station, false);
            }

            if (stations[1].Distance - stations[0].Distance < 0.99f)
            {
                throw new System.InvalidOperationException("Camera stations are not spaced one meter apart.");
            }
            foreach (var station in stations)
            {
                var actualDistance = Vector3.Distance(station.Camera.transform.position, targetObject.transform.position);
                if (Mathf.Abs(actualDistance - station.Distance) > 0.001f)
                {
                    throw new System.InvalidOperationException(
                        string.Format(
                            "The {0} m camera station is actually {1:0.####} m from the shader object.",
                            station.Distance,
                            actualDistance));
                }
            }
        }
        finally
        {
            if (savedLayers != null)
            {
                RestoreLayers(savedLayers);
            }
            previousContext.Restore();
            foreach (var station in stations)
            {
                SetDepthLightActive(station, false);
            }
        }
        if (!previousContext.IsCurrent())
        {
            throw new System.InvalidOperationException("The shader simulation globals were not restored after camera capture.");
        }
        foreach (var savedLayer in savedLayers)
        {
            if (savedLayer.Object != null && savedLayer.Object.layer != savedLayer.Layer)
            {
                throw new System.InvalidOperationException(
                    "The target object's original render layer was not restored after capture.");
            }
        }
    }

    private static void AssertRenderedTexture(RenderTexture renderTexture, float distance, string eye)
    {
        if (renderTexture == null || !renderTexture.IsCreated())
        {
            throw new System.InvalidOperationException(
                string.Format("Unity did not create the {0} eye image at {1} m.", eye, distance));
        }

        var previous = RenderTexture.active;
        var pixels = new Texture2D(renderTexture.width, renderTexture.height, TextureFormat.RGB24, false);
        try
        {
            RenderTexture.active = renderTexture;
            pixels.ReadPixels(new Rect(0f, 0f, renderTexture.width, renderTexture.height), 0, 0);
            pixels.Apply();
            var colors = pixels.GetPixels32();
            var first = colors[0];
            var hasVariation = false;
            for (var index = 1; index < colors.Length; index++)
            {
                if (colors[index].r != first.r || colors[index].g != first.g || colors[index].b != first.b)
                {
                    hasVariation = true;
                    break;
                }
            }
            if (!hasVariation)
            {
                throw new System.InvalidOperationException(
                    string.Format("The {0} eye image at {1} m is blank or has no scene detail.", eye, distance));
            }
        }
        finally
        {
            RenderTexture.active = previous;
            DestroyImmediate(pixels);
        }
    }

    private static int CountPixelDifferences(RenderTexture left, RenderTexture right)
    {
        var previous = RenderTexture.active;
        var leftPixels = new Texture2D(left.width, left.height, TextureFormat.RGB24, false);
        var rightPixels = new Texture2D(right.width, right.height, TextureFormat.RGB24, false);
        try
        {
            RenderTexture.active = left;
            leftPixels.ReadPixels(new Rect(0f, 0f, left.width, left.height), 0, 0);
            leftPixels.Apply();

            RenderTexture.active = right;
            rightPixels.ReadPixels(new Rect(0f, 0f, right.width, right.height), 0, 0);
            rightPixels.Apply();

            var leftColors = leftPixels.GetPixels32();
            var rightColors = rightPixels.GetPixels32();
            var differences = 0;
            for (var index = 0; index < leftColors.Length; index++)
            {
                if (leftColors[index].r != rightColors[index].r
                    || leftColors[index].g != rightColors[index].g
                    || leftColors[index].b != rightColors[index].b)
                {
                    differences++;
                }
            }
            return differences;
        }
        finally
        {
            RenderTexture.active = previous;
            DestroyImmediate(leftPixels);
            DestroyImmediate(rightPixels);
        }
    }

    private static int CountSideBySidePixelDifferences(RenderTexture image)
    {
        if (image.width % 2 != 0)
        {
            throw new System.InvalidOperationException("A side-by-side stereo image must have an even pixel width.");
        }

        var previous = RenderTexture.active;
        var pixels = new Texture2D(image.width, image.height, TextureFormat.RGB24, false);
        try
        {
            RenderTexture.active = image;
            pixels.ReadPixels(new Rect(0f, 0f, image.width, image.height), 0, 0);
            pixels.Apply();

            var colors = pixels.GetPixels32();
            var halfWidth = image.width / 2;
            var differences = 0;
            var leftFirst = colors[0];
            var rightFirst = colors[halfWidth];
            var leftHasDetail = false;
            var rightHasDetail = false;
            for (var y = 0; y < image.height; y++)
            {
                var rowStart = y * image.width;
                for (var x = 0; x < halfWidth; x++)
                {
                    var left = colors[rowStart + x];
                    var right = colors[rowStart + x + halfWidth];
                    leftHasDetail |= left.r != leftFirst.r || left.g != leftFirst.g || left.b != leftFirst.b;
                    rightHasDetail |= right.r != rightFirst.r || right.g != rightFirst.g || right.b != rightFirst.b;
                    if (left.r != right.r || left.g != right.g || left.b != right.b)
                    {
                        differences++;
                    }
                }
            }
            if (!leftHasDetail || !rightHasDetail)
            {
                throw new System.InvalidOperationException(
                    "Both eyes in the IF pimax side-by-side image must contain rendered scene detail.");
            }
            return differences;
        }
        finally
        {
            RenderTexture.active = previous;
            DestroyImmediate(pixels);
        }
    }
}
