using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class ShaderViewPreviewSetup
{
    private const string ShaderGuid = "0510857c96ead304b956c1a90edfcfe8";
    private const string MaterialAsset = "Assets/ShaderView/Materials/Center 1.mat";
    private const string SceneAsset = "Assets/ShaderView/ShaderViewPreview.unity";

    [MenuItem("Tools/Dosker Tools/ShaderView/Create exact Unity preview")]
    public static void CreatePreview()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            EditorUtility.DisplayDialog(
                "ShaderView",
                "Stop Play mode before creating the preview scene.",
                "OK");
            return;
        }

        var shader = AssetDatabase.LoadAssetAtPath<Shader>(AssetDatabase.GUIDToAssetPath(ShaderGuid));
        if (shader == null)
        {
            EditorUtility.DisplayDialog(
                "ShaderView",
                "Unity has not imported the shader yet. Wait for asset import to finish, then try again.",
                "OK");
            return;
        }

        if (ShaderUtil.ShaderHasError(shader) || !shader.isSupported)
        {
            EditorUtility.DisplayDialog(
                "ShaderView",
                "Unity reported shader compile errors or this graphics device does not support the shader. Check the Console before continuing.",
                "OK");
            return;
        }

        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
        {
            return;
        }

        EnsureAssetFolder("Assets/ShaderView/Materials");

        var material = AssetDatabase.LoadAssetAtPath<Material>(MaterialAsset);
        if (material == null)
        {
            material = new Material(shader)
            {
                name = "Center 1"
            };
            AssetDatabase.CreateAsset(material, MaterialAsset);
            AssetDatabase.SaveAssets();
        }
        else if (material.shader != shader)
        {
            material.shader = shader;
            EditorUtility.SetDirty(material);
            AssetDatabase.SaveAssets();
        }

        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        ConfigureLighting();
        CreateGround();
        CreateWorldObjects();
        CreateShaderObject(material);
        CreatePreviewCamera();

        if (!EditorSceneManager.SaveScene(scene, SceneAsset))
        {
            EditorUtility.DisplayDialog("ShaderView", "Unity could not save the preview scene.", "OK");
            return;
        }

        Selection.activeObject = material;
        EditorApplication.ExecuteMenuItem("Window/General/Game");
        EditorApplication.delayCall += () => EditorApplication.isPlaying = true;
        Debug.Log("ShaderView: real Unity preview started. The Game view uses Unity's renderer and the shader's original GrabPass.");
    }

    private static void ConfigureLighting()
    {
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.36f, 0.41f, 0.48f);

        var skyboxShader = Shader.Find("Skybox/Procedural");
        if (skyboxShader != null)
        {
            RenderSettings.skybox = new Material(skyboxShader);
        }
    }

    private static void CreateGround()
    {
        var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        ground.name = "World / ground";
        ground.transform.position = Vector3.zero;
        ground.transform.localScale = new Vector3(2f, 1f, 2f);
        ground.GetComponent<Renderer>().sharedMaterial =
            CreateStandardMaterial("Assets/ShaderView/Materials/WorldGround.mat", new Color(0.22f, 0.30f, 0.34f));

        var light = new GameObject("World / directional light");
        var directionalLight = light.AddComponent<Light>();
        directionalLight.type = LightType.Directional;
        directionalLight.intensity = 1.2f;
        light.transform.rotation = Quaternion.Euler(42f, -28f, 0f);
    }

    private static void CreateWorldObjects()
    {
        CreateCube("World / warm cube", new Vector3(-2.2f, 0.65f, 1.8f), new Vector3(1.2f, 1.3f, 1.2f),
            new Color(0.88f, 0.27f, 0.12f));
        CreateCube("World / blue cube", new Vector3(2.0f, 0.55f, 2.5f), new Vector3(1.1f, 1.1f, 1.1f),
            new Color(0.16f, 0.42f, 0.92f));
        CreateCube("World / yellow cube", new Vector3(0.3f, 0.4f, 4.0f), new Vector3(0.8f, 0.8f, 0.8f),
            new Color(0.92f, 0.70f, 0.14f));
    }

    private static void CreateCube(string name, Vector3 position, Vector3 scale, Color color)
    {
        var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.name = name;
        cube.transform.position = position;
        cube.transform.localScale = scale;
        cube.GetComponent<Renderer>().sharedMaterial = CreateStandardMaterial(
            "Assets/ShaderView/Materials/" + name.Replace(" / ", "_") + ".mat",
            color);
    }

    private static void CreateShaderObject(Material material)
    {
        var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        sphere.name = "Shader under test / ScreenSpace Ubershader";
        sphere.transform.position = new Vector3(0f, 1f, 0.8f);
        sphere.transform.localScale = Vector3.one * 1.7f;
        sphere.GetComponent<Renderer>().sharedMaterial = material;
    }

    private static void CreatePreviewCamera()
    {
        var cameraObject = new GameObject("Preview camera");
        cameraObject.tag = "MainCamera";
        cameraObject.transform.position = new Vector3(0f, 2.6f, -8f);
        cameraObject.transform.LookAt(new Vector3(0f, 1f, 1.4f));

        var camera = cameraObject.AddComponent<Camera>();
        camera.clearFlags = CameraClearFlags.Skybox;
        camera.fieldOfView = 48f;
        camera.nearClipPlane = 0.1f;
        camera.farClipPlane = 100f;
        camera.allowHDR = true;
        camera.allowMSAA = true;
    }

    private static Material CreateStandardMaterial(string assetPath, Color color)
    {
        var material = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
        if (material != null)
        {
            return material;
        }

        var shader = Shader.Find("Standard");
        if (shader == null)
        {
            throw new System.InvalidOperationException("Unity's built-in Standard shader could not be found.");
        }

        material = new Material(shader)
        {
            name = Path.GetFileNameWithoutExtension(assetPath),
            color = color
        };
        material.SetFloat("_Glossiness", 0.24f);
        AssetDatabase.CreateAsset(material, assetPath);
        return material;
    }

    private static void EnsureAssetFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path))
        {
            return;
        }

        var parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
        var folder = Path.GetFileName(path);
        if (string.IsNullOrEmpty(parent) || !AssetDatabase.IsValidFolder(parent))
        {
            throw new System.InvalidOperationException("Missing Unity asset parent folder: " + parent);
        }

        AssetDatabase.CreateFolder(parent, folder);
    }
}
