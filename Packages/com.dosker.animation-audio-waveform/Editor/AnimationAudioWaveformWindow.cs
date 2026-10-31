using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Audio;

// Janela usada apenas quando o painel não pode ser embutido na janela Animation (sem Harmony).
public sealed class AnimationAudioWaveformWindow : EditorWindow
{
    private const float FallbackLeftWidth = 220f;

    private float lastTime = -1f;
    private Rect lastShownArea;
    private AnimationClip lastClip;

    [MenuItem("Tools/Dosker Tools/Animation Audio Waveform")]
    public static void OpenWindow()
    {
        if (AnimationAudioHarmonyPatch.Active)
        {
            var settings = AnimationAudioSettings.Instance;
            settings.panelEnabled = !settings.panelEnabled;
            settings.Save();
            AnimationWindowBridge.RepaintAll();
            return;
        }

        var window = GetWindow<AnimationAudioWaveformWindow>("Audio Waveform");
        window.minSize = new Vector2(420f, 120f);
        window.Show();
    }

    private void OnEnable()
    {
        EditorApplication.update += HandleEditorUpdate;
    }

    private void OnDisable()
    {
        EditorApplication.update -= HandleEditorUpdate;
    }

    private void HandleEditorUpdate()
    {
        var animationWindow = AnimationWindowBridge.FindTargetWindow();
        if (animationWindow == null)
        {
            return;
        }

        var time = animationWindow.time;
        var shownArea = AnimationWindowBridge.GetShownArea(animationWindow);
        var clip = animationWindow.animationClip;
        if (!Mathf.Approximately(time, lastTime) || shownArea != lastShownArea || clip != lastClip)
        {
            lastTime = time;
            lastShownArea = shownArea;
            lastClip = clip;
            Repaint();
        }
    }

    private void OnGUI()
    {
        var animationWindow = AnimationWindowBridge.FindTargetWindow();
        if (animationWindow == null)
        {
            EditorGUILayout.HelpBox("Abra a janela Animation (Window > Animation > Animation) e selecione um clip.", MessageType.Info);
            return;
        }

        var hierarchyWidth = AnimationWindowBridge.GetHierarchyWidth(animationWindow);
        var contentWidth = AnimationWindowBridge.GetContentWidth(animationWindow);
        var timeLeft = animationWindow.position.x + hierarchyWidth - position.x;
        var timeWidth = contentWidth - AnimationAudioPanel.ScrollbarWidth;
        var aligned = hierarchyWidth > 0f
            && timeWidth > 0f
            && timeLeft >= 120f
            && timeLeft + timeWidth <= position.width;
        if (!aligned)
        {
            timeLeft = FallbackLeftWidth;
            timeWidth = Mathf.Max(1f, position.width - FallbackLeftWidth - AnimationAudioPanel.ScrollbarWidth);
        }

        AnimationAudioPanel.Draw(new Rect(0f, 0f, position.width, position.height), animationWindow, timeLeft, timeWidth, false);
    }
}

// Acesso por reflexão ao estado interno da janela Animation.
internal static class AnimationWindowBridge
{
    private const BindingFlags AnyMember = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private static readonly Assembly EditorAssembly = typeof(AnimationWindow).Assembly;
    private static readonly Type AnimEditorType = EditorAssembly.GetType("UnityEditor.AnimEditor");
    private static readonly Type StateType = EditorAssembly.GetType("UnityEditorInternal.AnimationWindowState");
    private static readonly Type ZoomableAreaType = EditorAssembly.GetType("UnityEditor.ZoomableArea");

    private static readonly PropertyInfo StateProperty = typeof(AnimationWindow).GetProperty("state", AnyMember);
    private static readonly MethodInfo AllWindowsMethod = typeof(AnimationWindow).GetMethod("GetAllAnimationWindows", AnyMember);
    private static readonly PropertyInfo EditorProperty = typeof(AnimationWindow).GetProperty("animEditor", AnyMember);
    private static readonly PropertyInfo HierarchyWidthProperty = AnimEditorType?.GetProperty("hierarchyWidth", AnyMember);
    private static readonly PropertyInfo ContentWidthProperty = AnimEditorType?.GetProperty("contentWidth", AnyMember);
    private static readonly PropertyInfo TimeAreaProperty = StateType?.GetProperty("timeArea", AnyMember);
    private static readonly PropertyInfo RootProperty = StateType?.GetProperty("activeRootGameObject", AnyMember);
    private static readonly PropertyInfo FrameRateProperty = StateType?.GetProperty("frameRate", AnyMember);
    private static readonly PropertyInfo MinTimeProperty = StateType?.GetProperty("minTime", AnyMember);
    private static readonly PropertyInfo ShownAreaProperty = ZoomableAreaType?.GetProperty("shownArea", AnyMember);

    public static readonly MethodInfo EditorGuiMethod = AnimEditorType?.GetMethod("OnAnimEditorGUI", AnyMember);

    public static IEnumerable<string> MissingMembers()
    {
        var members = new (string Name, object Member)[]
        {
            ("AnimationWindow.state", StateProperty),
            ("AnimationWindow.GetAllAnimationWindows", AllWindowsMethod),
            ("AnimationWindow.animEditor", EditorProperty),
            ("AnimEditor.hierarchyWidth", HierarchyWidthProperty),
            ("AnimEditor.contentWidth", ContentWidthProperty),
            ("AnimEditor.OnAnimEditorGUI", EditorGuiMethod),
            ("AnimationWindowState.timeArea", TimeAreaProperty),
            ("AnimationWindowState.activeRootGameObject", RootProperty),
            ("AnimationWindowState.frameRate", FrameRateProperty),
            ("AnimationWindowState.minTime", MinTimeProperty),
            ("ZoomableArea.shownArea", ShownAreaProperty),
        };
        foreach (var member in members)
        {
            if (member.Member == null)
            {
                yield return member.Name;
            }
        }
    }

    public static Type FindType(string fullName)
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

    public static IEnumerable<AnimationWindow> AllWindows()
    {
        if (AllWindowsMethod != null && AllWindowsMethod.Invoke(null, null) is IEnumerable windows)
        {
            foreach (var window in windows)
            {
                if (window is AnimationWindow animationWindow && animationWindow != null)
                {
                    yield return animationWindow;
                }
            }
            yield break;
        }

        foreach (var window in Resources.FindObjectsOfTypeAll<AnimationWindow>())
        {
            yield return window;
        }
    }

    public static AnimationWindow FindPlayingWindow()
    {
        foreach (var window in AllWindows())
        {
            if (window.playing)
            {
                return window;
            }
        }
        return null;
    }

    public static AnimationWindow FindTargetWindow()
    {
        var playing = FindPlayingWindow();
        if (playing != null)
        {
            return playing;
        }

        if (EditorWindow.focusedWindow is AnimationWindow focused)
        {
            return focused;
        }

        AnimationWindow first = null;
        foreach (var window in AllWindows())
        {
            if (window.animationClip != null)
            {
                return window;
            }
            first = first ?? window;
        }
        return first;
    }

    public static bool AnyPreviewing()
    {
        foreach (var window in AllWindows())
        {
            if (window.previewing)
            {
                return true;
            }
        }
        return false;
    }

    public static void RepaintAll()
    {
        foreach (var window in AllWindows())
        {
            window.Repaint();
        }
    }

    private static object GetState(AnimationWindow window)
    {
        return StateProperty?.GetValue(window);
    }

    public static GameObject GetRoot(AnimationWindow window)
    {
        var state = GetState(window);
        return state == null || RootProperty == null ? null : RootProperty.GetValue(state) as GameObject;
    }

    public static float GetFrameRate(AnimationWindow window)
    {
        var state = GetState(window);
        if (state != null && FrameRateProperty != null && FrameRateProperty.GetValue(state) is float frameRate && frameRate > 0f)
        {
            return frameRate;
        }
        var clip = window.animationClip;
        return clip != null && clip.frameRate > 0f ? clip.frameRate : 60f;
    }

    public static float GetMinTime(AnimationWindow window)
    {
        var state = GetState(window);
        return state != null && MinTimeProperty != null && MinTimeProperty.GetValue(state) is float minTime ? minTime : 0f;
    }

    public static Rect GetShownArea(AnimationWindow window)
    {
        var state = GetState(window);
        var timeArea = state == null || TimeAreaProperty == null ? null : TimeAreaProperty.GetValue(state);
        if (timeArea != null && ShownAreaProperty != null && ShownAreaProperty.GetValue(timeArea) is Rect shownArea && shownArea.width > 0f)
        {
            return shownArea;
        }
        var clip = window.animationClip;
        return new Rect(0f, 0f, clip != null ? Mathf.Max(clip.length, 1f) : 1f, 1f);
    }

    private static float GetEditorFloat(AnimationWindow window, PropertyInfo property)
    {
        var editor = EditorProperty?.GetValue(window);
        return editor != null && property != null && property.GetValue(editor) is float value ? value : 0f;
    }

    public static float GetHierarchyWidth(AnimationWindow window)
    {
        return GetEditorFloat(window, HierarchyWidthProperty);
    }

    public static float GetContentWidth(AnimationWindow window)
    {
        return GetEditorFloat(window, ContentWidthProperty);
    }
}

// ---------------------------------------------------------------- Configurações

[Serializable]
internal sealed class AnimationAudioManualTrack
{
    public string audioGuid;
    public float offset;
    public bool visible = true;
    public bool play = true;
    public Color color;
}

[Serializable]
internal sealed class AnimationAudioTrackOverride
{
    public string key;
    public bool visible = true;
    public bool play;
    public Color color;
}

[Serializable]
internal sealed class AnimationAudioClipEntry
{
    public string clipKey;
    public List<AnimationAudioManualTrack> manualTracks = new List<AnimationAudioManualTrack>();
    public List<AnimationAudioTrackOverride> overrides = new List<AnimationAudioTrackOverride>();
}

// Fica em UserSettings: não altera clips, cenas nem assets do projeto.
[Serializable]
internal sealed class AnimationAudioSettings
{
    private const string FilePath = "UserSettings/AnimationAudioWaveform.json";

    private static AnimationAudioSettings instance;

    public bool panelEnabled = true;
    public bool collapsed;
    public bool playbackEnabled = true;
    public bool falloffEnabled = true;
    public float panelHeight = 170f;
    public List<AnimationAudioClipEntry> clips = new List<AnimationAudioClipEntry>();

    public static AnimationAudioSettings Instance
    {
        get
        {
            if (instance == null)
            {
                instance = Load();
            }
            return instance;
        }
    }

    private static AnimationAudioSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonUtility.FromJson<AnimationAudioSettings>(File.ReadAllText(FilePath)) ?? new AnimationAudioSettings();
            }
        }
        catch (Exception exception)
        {
            Debug.LogWarning("Animation Audio Waveform: configurações ignoradas. " + exception.Message);
        }
        return new AnimationAudioSettings();
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(FilePath, JsonUtility.ToJson(this, true));
        }
        catch (Exception exception)
        {
            Debug.LogWarning("Animation Audio Waveform: não foi possível salvar as configurações. " + exception.Message);
        }
    }

    public AnimationAudioClipEntry GetEntry(string clipKey, bool create)
    {
        foreach (var entry in clips)
        {
            if (entry.clipKey == clipKey)
            {
                return entry;
            }
        }

        if (!create)
        {
            return null;
        }

        var created = new AnimationAudioClipEntry { clipKey = clipKey };
        clips.Add(created);
        return created;
    }
}

// ---------------------------------------------------------------- Faixas

internal struct AnimationAudioSegment
{
    public float Start;
    public float End;
}

internal sealed class AnimationAudioTrack
{
    public string Key;
    public string Label;
    public AudioClip Clip;
    public AudioSource Source;
    public float Volume = 1f;
    public float Pitch = 1f;
    public AudioMixerGroup Mixer;
    public bool Loop;
    public int ColorIndex;
    public AnimationAudioManualTrack Manual;
    public AnimationAudioTrackOverride Override;
    public readonly List<AnimationAudioSegment> Segments = new List<AnimationAudioSegment>();

    public bool IsManual
    {
        get { return Manual != null; }
    }

    public bool Visible
    {
        get { return IsManual ? Manual.visible : Override.visible; }
    }

    public bool Play
    {
        get { return IsManual ? Manual.play : Override.play; }
    }

    public Color Color
    {
        get
        {
            var color = IsManual ? Manual.color : Override.color;
            return color.a > 0f ? color : AnimationAudioPanel.Palette[ColorIndex % AnimationAudioPanel.Palette.Length];
        }
    }

    public float PlaybackRate
    {
        get { return Mathf.Max(0.01f, Mathf.Abs(Pitch)); }
    }

    public float Duration
    {
        get { return Clip != null ? Clip.length / PlaybackRate : 0f; }
    }

    // A posição vem sempre do ponto de ativação do trecho, nunca de um relógio próprio.
    public bool TryGetPosition(float time, out float position)
    {
        position = 0f;
        if (Clip == null || Clip.length <= 0f)
        {
            return false;
        }

        foreach (var segment in Segments)
        {
            if (time < segment.Start || time >= segment.End)
            {
                continue;
            }

            position = (time - segment.Start) * PlaybackRate;
            if (Loop)
            {
                position %= Clip.length;
                return true;
            }
            return position < Clip.length;
        }
        return false;
    }
}

internal sealed class AnimationAudioModel
{
    private const double RefreshInterval = 1.0;
    private const string ActiveProperty = "m_IsActive";
    private const string EnabledProperty = "m_Enabled";
    private const float ActiveEpsilon = 0.001f;

    private static readonly Dictionary<int, AnimationAudioModel> Models = new Dictionary<int, AnimationAudioModel>();
    private static int generation;

    public AnimationClip Clip;
    public GameObject Root;
    public string ClipKey;
    public string Signature;
    public readonly List<AnimationAudioTrack> Tracks = new List<AnimationAudioTrack>();

    private int builtGeneration;
    private double builtAt;

    static AnimationAudioModel()
    {
        AnimationUtility.onCurveWasModified += (clip, binding, type) => MarkDirty();
        EditorApplication.hierarchyChanged += MarkDirty;
        Undo.undoRedoPerformed += MarkDirty;
    }

    public static void MarkDirty()
    {
        generation++;
    }

    public static AnimationAudioModel For(AnimationWindow window)
    {
        var id = window.GetInstanceID();
        var clip = window.animationClip;
        if (clip == null)
        {
            Models.Remove(id);
            return null;
        }

        var root = AnimationWindowBridge.GetRoot(window);
        var now = EditorApplication.timeSinceStartup;
        if (Models.TryGetValue(id, out var current) && current.Clip == clip && current.Root == root)
        {
            // O preview ativa objetos a cada frame: durante o play só um clip novo reconstrói as faixas.
            var stale = current.builtGeneration != generation || now - current.builtAt >= RefreshInterval;
            if (window.playing || !stale)
            {
                return current;
            }
        }

        var model = Create(clip, root);
        if (current != null && current.Clip == clip && current.Root == root && current.Signature == model.Signature)
        {
            current.builtGeneration = model.builtGeneration;
            current.builtAt = model.builtAt;
            return current;
        }

        Models[id] = model;
        return model;
    }

    public static AnimationAudioModel Create(AnimationClip clip, GameObject root)
    {
        var model = new AnimationAudioModel
        {
            Clip = clip,
            Root = root,
            ClipKey = GetClipKey(clip),
            builtGeneration = generation,
            builtAt = EditorApplication.timeSinceStartup,
        };
        var entry = AnimationAudioSettings.Instance.GetEntry(model.ClipKey, false);
        if (root != null)
        {
            model.BuildAutomaticTracks(entry);
        }
        model.BuildManualTracks(entry);
        model.Signature = model.BuildSignature();
        return model;
    }

    private static string GetClipKey(AnimationClip clip)
    {
        if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(clip, out var guid, out long localId))
        {
            return guid + ":" + localId;
        }
        return "instance:" + clip.GetInstanceID();
    }

    // Mesma regra do Unity ao converter a curva para bool; confirmada por AnimationAudioValidation.
    public static bool IsActiveValue(float value)
    {
        return Mathf.Abs(value) > ActiveEpsilon;
    }

    private void BuildAutomaticTracks(AnimationAudioClipEntry entry)
    {
        var activeCurves = new Dictionary<GameObject, AnimationCurve>();
        var enabledCurves = new Dictionary<AudioSource, AnimationCurve>();
        var sources = new List<AudioSource>();
        var seen = new HashSet<AudioSource>();

        foreach (var binding in AnimationUtility.GetCurveBindings(Clip))
        {
            var isActive = binding.type == typeof(GameObject) && binding.propertyName == ActiveProperty;
            var isEnabled = binding.type == typeof(AudioSource) && binding.propertyName == EnabledProperty;
            if (!isActive && !isEnabled)
            {
                continue;
            }

            var target = AnimationUtility.GetAnimatedObject(Root, binding);
            var curve = AnimationUtility.GetEditorCurve(Clip, binding);
            if (target == null || curve == null)
            {
                continue;
            }

            if (target is GameObject gameObject)
            {
                activeCurves[gameObject] = curve;
                foreach (var source in gameObject.GetComponentsInChildren<AudioSource>(true))
                {
                    if (seen.Add(source))
                    {
                        sources.Add(source);
                    }
                }
            }
            else if (target is AudioSource audioSource)
            {
                enabledCurves[audioSource] = curve;
                if (seen.Add(audioSource))
                {
                    sources.Add(audioSource);
                }
            }
        }

        foreach (var source in sources)
        {
            if (source.clip == null)
            {
                continue;
            }

            enabledCurves.TryGetValue(source, out var enabledCurve);
            if (!TryCollectCurves(source, activeCurves, out var curves) || (enabledCurve == null && !source.enabled))
            {
                continue;
            }

            var track = CreateAutomaticTrack(source, entry);
            AddActivationSegments(track, curves, enabledCurve);
            if (track.Segments.Count > 0)
            {
                Tracks.Add(track);
            }
        }
    }

    private bool TryCollectCurves(AudioSource source, Dictionary<GameObject, AnimationCurve> activeCurves, out List<AnimationCurve> curves)
    {
        curves = new List<AnimationCurve>();
        for (var transform = source.transform; transform != null; transform = transform.parent)
        {
            if (activeCurves.TryGetValue(transform.gameObject, out var curve))
            {
                curves.Add(curve);
            }
            else if (!transform.gameObject.activeSelf)
            {
                return false;
            }

            if (transform == Root.transform)
            {
                break;
            }
        }
        return true;
    }

    private AnimationAudioTrack CreateAutomaticTrack(AudioSource source, AnimationAudioClipEntry entry)
    {
        var key = AnimationUtility.CalculateTransformPath(source.transform, Root.transform) + "|" + source.clip.name;
        AnimationAudioTrackOverride trackOverride = null;
        if (entry != null)
        {
            trackOverride = entry.overrides.Find(item => item.key == key);
        }

        return new AnimationAudioTrack
        {
            Key = key,
            Label = source.gameObject.name + " · " + source.clip.name,
            Clip = source.clip,
            Source = source,
            Volume = source.volume,
            Pitch = source.pitch,
            Mixer = source.outputAudioMixerGroup,
            Loop = source.loop,
            ColorIndex = Tracks.Count,
            Override = trackOverride ?? new AnimationAudioTrackOverride { key = key, play = source.playOnAwake },
        };
    }

    private void AddActivationSegments(AnimationAudioTrack track, List<AnimationCurve> curves, AnimationCurve enabledCurve)
    {
        var times = new SortedSet<float> { 0f };
        foreach (var curve in curves)
        {
            AddEvaluationTimes(curve, times);
        }
        if (enabledCurve != null)
        {
            AddEvaluationTimes(enabledCurve, times);
        }

        var wasActive = false;
        var start = 0f;
        foreach (var time in times)
        {
            var active = enabledCurve == null || IsActiveValue(enabledCurve.Evaluate(time));
            foreach (var curve in curves)
            {
                active = active && IsActiveValue(curve.Evaluate(time));
            }

            if (active && !wasActive)
            {
                start = time;
            }
            else if (!active && wasActive)
            {
                AddSegment(track, start, time);
            }
            wasActive = active;
        }

        if (wasActive)
        {
            AddSegment(track, start, float.PositiveInfinity);
        }
    }

    private void AddEvaluationTimes(AnimationCurve curve, SortedSet<float> times)
    {
        var keys = curve.keys;
        var stepped = true;
        for (var index = 0; index < keys.Length; index++)
        {
            times.Add(keys[index].time);
            if (index + 1 < keys.Length
                && !float.IsInfinity(keys[index].outTangent)
                && !Mathf.Approximately(keys[index].value, keys[index + 1].value))
            {
                stepped = false;
            }
        }

        if (stepped || keys.Length == 0)
        {
            return;
        }

        var frameRate = Clip.frameRate > 0f ? Clip.frameRate : 60f;
        var lastKeyTime = keys[keys.Length - 1].time;
        for (var frame = Mathf.CeilToInt(keys[0].time * frameRate); frame / frameRate < lastKeyTime; frame++)
        {
            times.Add(frame / frameRate);
        }
    }

    private void AddSegment(AnimationAudioTrack track, float start, float deactivation)
    {
        var end = track.Loop
            ? Mathf.Min(deactivation, Mathf.Max(Clip.length, start + track.Duration))
            : Mathf.Min(deactivation, start + track.Duration);
        if (end - start > 0.0001f)
        {
            track.Segments.Add(new AnimationAudioSegment { Start = start, End = end });
        }
    }

    private void BuildManualTracks(AnimationAudioClipEntry entry)
    {
        if (entry == null)
        {
            return;
        }

        foreach (var manual in entry.manualTracks)
        {
            var path = AssetDatabase.GUIDToAssetPath(manual.audioGuid);
            var clip = string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<AudioClip>(path);
            if (clip == null)
            {
                continue;
            }

            var track = new AnimationAudioTrack
            {
                Key = "manual|" + manual.audioGuid,
                Label = clip.name,
                Clip = clip,
                ColorIndex = Tracks.Count,
                Manual = manual,
            };
            track.Segments.Add(new AnimationAudioSegment { Start = manual.offset, End = manual.offset + clip.length });
            Tracks.Add(track);
        }
    }

    private string BuildSignature()
    {
        var builder = new StringBuilder();
        foreach (var track in Tracks)
        {
            builder.Append(track.Key).Append(';').Append(track.Clip.GetInstanceID()).Append(';');
            builder.Append(track.Volume).Append(';').Append(track.Pitch).Append(';').Append(track.Loop).Append(';');
            foreach (var segment in track.Segments)
            {
                builder.Append(segment.Start).Append('-').Append(segment.End).Append(';');
            }
            builder.Append('|');
        }
        return builder.ToString();
    }

    public void AddManualTrack(AudioClip clip)
    {
        if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(clip, out var guid, out long _))
        {
            return;
        }

        var settings = AnimationAudioSettings.Instance;
        var entry = settings.GetEntry(ClipKey, true);
        if (entry.manualTracks.Find(item => item.audioGuid == guid) == null)
        {
            entry.manualTracks.Add(new AnimationAudioManualTrack { audioGuid = guid });
            settings.Save();
        }
        MarkDirty();
        builtAt = 0.0;
    }

    public void RemoveManualTrack(AnimationAudioTrack track)
    {
        var settings = AnimationAudioSettings.Instance;
        var entry = settings.GetEntry(ClipKey, false);
        if (entry != null && entry.manualTracks.Remove(track.Manual))
        {
            settings.Save();
        }
        MarkDirty();
        builtAt = 0.0;
    }

    public void SetVisible(AnimationAudioTrack track, bool visible)
    {
        if (track.IsManual)
        {
            track.Manual.visible = visible;
        }
        else
        {
            track.Override.visible = visible;
        }
        SaveTrack(track);
    }

    public void SetPlay(AnimationAudioTrack track, bool play)
    {
        if (track.IsManual)
        {
            track.Manual.play = play;
        }
        else
        {
            track.Override.play = play;
        }
        SaveTrack(track);
    }

    public void SetColor(AnimationAudioTrack track, Color color)
    {
        if (track.IsManual)
        {
            track.Manual.color = color;
        }
        else
        {
            track.Override.color = color;
        }
        SaveTrack(track);
    }

    public void SetOffset(AnimationAudioTrack track, float offset, bool save)
    {
        track.Manual.offset = Mathf.Max(0f, offset);
        track.Segments[0] = new AnimationAudioSegment
        {
            Start = track.Manual.offset,
            End = track.Manual.offset + track.Clip.length,
        };
        if (save)
        {
            SaveTrack(track);
        }
    }

    public void SaveTrack(AnimationAudioTrack track)
    {
        var settings = AnimationAudioSettings.Instance;
        if (!track.IsManual)
        {
            var entry = settings.GetEntry(ClipKey, true);
            if (!entry.overrides.Contains(track.Override))
            {
                entry.overrides.Add(track.Override);
            }
        }
        settings.Save();
    }
}

// ---------------------------------------------------------------- Waveform

internal sealed class AnimationAudioPeaks
{
    private const int BucketsPerSecond = 400;
    private const int MaxBuckets = 400000;
    private const int ChunkSamples = 65536;

    private static readonly MethodInfo MinMaxMethod = typeof(AudioImporter).Assembly
        .GetType("UnityEditor.AudioUtil")?
        .GetMethod("GetMinMaxData", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

    private static readonly Dictionary<AudioClip, AnimationAudioPeaks> Cache = new Dictionary<AudioClip, AnimationAudioPeaks>();

    public float[] Min;
    public float[] Max;
    public float Gain = 1f;

    public bool HasData
    {
        get { return Min != null && Min.Length > 0; }
    }

    // Retorna null enquanto o clip ainda está carregando; a chamada seguinte tenta de novo.
    public static AnimationAudioPeaks Get(AudioClip clip)
    {
        if (clip == null)
        {
            return null;
        }

        if (Cache.TryGetValue(clip, out var cached))
        {
            return cached;
        }

        AnimationAudioPeaks peaks;
        if (clip.loadType == AudioClipLoadType.DecompressOnLoad)
        {
            if (clip.loadState != AudioDataLoadState.Loaded)
            {
                clip.LoadAudioData();
            }
            if (clip.loadState == AudioDataLoadState.Unloaded || clip.loadState == AudioDataLoadState.Loading)
            {
                return null;
            }
            peaks = clip.loadState == AudioDataLoadState.Loaded ? ReadSamples(clip) : new AnimationAudioPeaks();
        }
        else
        {
            peaks = ReadImporterData(clip);
        }

        Cache[clip] = peaks;
        return peaks;
    }

    private static AnimationAudioPeaks ReadSamples(AudioClip clip)
    {
        var peaks = new AnimationAudioPeaks();
        var channels = Mathf.Max(1, clip.channels);
        var samples = clip.samples;
        if (samples <= 0 || clip.frequency <= 0)
        {
            return peaks;
        }

        var bucketCount = Mathf.Clamp(Mathf.CeilToInt((float)samples / clip.frequency * BucketsPerSecond), 1, MaxBuckets);
        peaks.Allocate(bucketCount);
        var buffer = new float[Mathf.Min(ChunkSamples, samples) * channels];
        for (var offset = 0; offset < samples; offset += ChunkSamples)
        {
            if (!clip.GetData(buffer, offset))
            {
                return new AnimationAudioPeaks();
            }

            var count = Mathf.Min(ChunkSamples, samples - offset);
            for (var sample = 0; sample < count; sample++)
            {
                var bucket = (int)((long)(offset + sample) * bucketCount / samples);
                for (var channel = 0; channel < channels; channel++)
                {
                    peaks.Accumulate(bucket, buffer[sample * channels + channel]);
                }
            }
        }

        peaks.Finish();
        return peaks;
    }

    // Clips comprimidos ou em streaming não permitem GetData: usa o resumo do inspector de áudio.
    private static AnimationAudioPeaks ReadImporterData(AudioClip clip)
    {
        var peaks = new AnimationAudioPeaks();
        var importer = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(clip)) as AudioImporter;
        if (importer == null || MinMaxMethod == null || !(MinMaxMethod.Invoke(null, new object[] { importer }) is float[] data))
        {
            return peaks;
        }

        var channels = Mathf.Max(1, clip.channels);
        var bucketCount = data.Length / (2 * channels);
        if (bucketCount <= 0)
        {
            return peaks;
        }

        peaks.Allocate(bucketCount);
        for (var bucket = 0; bucket < bucketCount; bucket++)
        {
            for (var channel = 0; channel < channels; channel++)
            {
                var index = (bucket * channels + channel) * 2;
                peaks.Accumulate(bucket, data[index]);
                peaks.Accumulate(bucket, data[index + 1]);
            }
        }

        peaks.Finish();
        return peaks;
    }

    private void Allocate(int bucketCount)
    {
        Min = new float[bucketCount];
        Max = new float[bucketCount];
    }

    private void Accumulate(int bucket, float value)
    {
        if (value < Min[bucket])
        {
            Min[bucket] = value;
        }
        if (value > Max[bucket])
        {
            Max[bucket] = value;
        }
    }

    private void Finish()
    {
        var peak = 0f;
        for (var bucket = 0; bucket < Min.Length; bucket++)
        {
            peak = Mathf.Max(peak, Mathf.Abs(Min[bucket]), Mathf.Abs(Max[bucket]));
        }
        Gain = peak > 0.001f ? 1f / peak : 1f;
    }

    public void GetRange(float from, float to, out float min, out float max)
    {
        var first = Mathf.Clamp((int)(from * Min.Length), 0, Min.Length - 1);
        var last = Mathf.Clamp(Mathf.CeilToInt(to * Min.Length), first + 1, Min.Length);
        min = 0f;
        max = 0f;
        for (var bucket = first; bucket < last; bucket++)
        {
            min = Mathf.Min(min, Min[bucket]);
            max = Mathf.Max(max, Max[bucket]);
        }
    }
}

// ---------------------------------------------------------------- Falloff

internal struct AnimationAudioMix
{
    public float Volume;
    public float Pan;
}

// As vozes continuam 2D: fora do Play mode o ouvinte real não é controlável, então volume e pan são calculados aqui.
internal static class AnimationAudioFalloff
{
    private const string AudioManagerPath = "ProjectSettings/AudioManager.asset";
    private const string RolloffScaleName = "Rolloff Scale";
    private const float MinimumDistance = 0.0001f;

    private static float rolloffScale = -1f;

    public static float RolloffScale
    {
        get
        {
            if (rolloffScale < 0f)
            {
                rolloffScale = LoadRolloffScale();
            }
            return rolloffScale;
        }
    }

    private static float LoadRolloffScale()
    {
        var assets = AssetDatabase.LoadAllAssetsAtPath(AudioManagerPath);
        if (assets.Length == 0 || assets[0] == null)
        {
            return 1f;
        }

        using (var audioManager = new SerializedObject(assets[0]))
        {
            var property = audioManager.FindProperty(RolloffScaleName);
            return property != null ? Mathf.Max(0f, property.floatValue) : 1f;
        }
    }

    public static AnimationAudioMix GetMix(AnimationAudioTrack track)
    {
        if (!AnimationAudioSettings.Instance.falloffEnabled || track.Source == null || !TryGetListener(out var listener))
        {
            return new AnimationAudioMix { Volume = 1f };
        }
        return GetMix(track.Source, listener.position, listener.right, track.Clip.channels);
    }

    public static float GetGain(AudioSource source, Vector3 listener)
    {
        var distance = Vector3.Distance(source.transform.position, listener);
        var normalized = GetNormalizedDistance(source, distance);
        return Mathf.Lerp(1f, GetAttenuation(source, distance, normalized), GetBlend(source, normalized));
    }

    // Leis medidas na saída real do Unity: cada canal mistura 2D e 3D, e o ganho da distância vem à parte.
    public static AnimationAudioMix GetMix(AudioSource source, Vector3 listener, Vector3 listenerRight, int channels)
    {
        var offset = source.transform.position - listener;
        var blend = GetBlend(source, GetNormalizedDistance(source, offset.magnitude));
        var spatialPan = GetSpatialPan(Vector3.Dot(offset.normalized, listenerRight));
        var stereo = channels > 1;

        GetChannels(source.panStereo, stereo, false, out var flatLeft, out var flatRight);
        GetChannels(spatialPan, stereo, true, out var spatialLeft, out var spatialRight);
        var left = Mathf.Lerp(flatLeft, spatialLeft, blend);
        var right = Mathf.Lerp(flatRight, spatialRight, blend);
        return ToMix(left, right, GetGain(source, listener), stereo);
    }

    private static float GetNormalizedDistance(AudioSource source, float distance)
    {
        return distance / Mathf.Max(source.maxDistance, MinimumDistance);
    }

    private static float GetBlend(AudioSource source, float normalized)
    {
        return source.GetCustomCurve(AudioSourceCurveType.SpatialBlend).Evaluate(normalized);
    }

    private static bool TryGetListener(out Transform listener)
    {
        var sceneView = SceneView.lastActiveSceneView;
        var camera = sceneView != null ? sceneView.camera : null;
        if (camera == null)
        {
            camera = Camera.main;
        }

        listener = camera != null ? camera.transform : null;
        return camera != null;
    }

    private static float GetAttenuation(AudioSource source, float distance, float normalized)
    {
        switch (source.rolloffMode)
        {
            case AudioRolloffMode.Custom:
                return source.GetCustomCurve(AudioSourceCurveType.CustomRolloff).Evaluate(normalized);
            case AudioRolloffMode.Linear:
                return distance <= source.minDistance ? 1f : Mathf.InverseLerp(source.maxDistance, source.minDistance, distance);
            default:
                return GetLogarithmicAttenuation(distance, source.minDistance);
        }
    }

    // Ao contrário do linear, continua caindo depois de maxDistance.
    private static float GetLogarithmicAttenuation(float distance, float minDistance)
    {
        if (distance <= minDistance)
        {
            return 1f;
        }

        var scaled = (distance - minDistance) * RolloffScale + minDistance;
        return minDistance / Mathf.Max(scaled, MinimumDistance);
    }

    // side é a componente da direção do som no eixo direito da câmera: frente e trás soam iguais.
    private static float GetSpatialPan(float side)
    {
        var center = 1f - side * side;
        return Mathf.Sign(side) * Mathf.Sqrt(Mathf.Max(0f, 1f - center * center));
    }

    private static void GetChannels(float pan, bool stereo, bool spatial, out float left, out float right)
    {
        if (stereo && !spatial)
        {
            left = Mathf.Min(1f, 1f - pan);
            right = Mathf.Min(1f, 1f + pan);
            return;
        }

        left = Mathf.Sqrt((1f - pan) * 0.5f);
        right = Mathf.Sqrt((1f + pan) * 0.5f);
        if (stereo)
        {
            left = Mathf.Min(1f, left * Mathf.Sqrt(2f));
            right = Mathf.Min(1f, right * Mathf.Sqrt(2f));
        }
    }

    // Em clip estéreo o panStereo é só balanço: ao contrário do 3D real, um canal nunca passa para o outro lado.
    private static AnimationAudioMix ToMix(float left, float right, float gain, bool stereo)
    {
        if (stereo)
        {
            var peak = Mathf.Max(left, right);
            return peak > 0f
                ? new AnimationAudioMix { Volume = gain * peak, Pan = (right - left) / peak }
                : default;
        }

        var power = left * left + right * right;
        return power > 0f
            ? new AnimationAudioMix { Volume = gain * Mathf.Sqrt(power), Pan = (right * right - left * left) / power }
            : default;
    }
}

// ---------------------------------------------------------------- Reprodução

// Independe do painel: basta a animação estar rodando. As fontes reais nunca são tocadas.
[InitializeOnLoad]
internal static class AnimationAudioPlayback
{
    private const float ContinuityTolerance = 0.2f;
    private const float ResyncThreshold = 0.08f;
    private const float EndMargin = 0.03f;
    private const int RequiredContinuousTicks = 2;

    private static readonly Dictionary<string, AudioSource> Voices = new Dictionary<string, AudioSource>();

    private static AnimationWindow trackedWindow;
    private static AnimationClip trackedClip;
    private static float lastTime;
    private static double lastAdvance;
    private static int continuousTicks;

    static AnimationAudioPlayback()
    {
        EditorApplication.update += HandleEditorUpdate;
        EditorApplication.playModeStateChanged += HandlePlayModeChanged;
        EditorApplication.quitting += DestroyVoices;
        AssemblyReloadEvents.beforeAssemblyReload += DestroyVoices;
    }

    private static void HandlePlayModeChanged(PlayModeStateChange change)
    {
        ResetContinuity();
        DestroyVoices();
    }

    private static void HandleEditorUpdate()
    {
        var window = AnimationAudioSettings.Instance.playbackEnabled && !EditorApplication.isPlayingOrWillChangePlaymode
            ? AnimationWindowBridge.FindPlayingWindow()
            : null;

        // Pausado, parado ou arrastando a playhead: silêncio já neste update.
        if (window == null)
        {
            ResetContinuity();
            if (Voices.Count > 0)
            {
                if (AnimationWindowBridge.AnyPreviewing())
                {
                    StopVoices();
                }
                else
                {
                    DestroyVoices();
                }
            }
            return;
        }

        if (!IsRunningContinuously(window))
        {
            StopVoices();
            return;
        }

        var model = AnimationAudioModel.For(window);
        if (model != null)
        {
            Synchronize(model, window.time);
        }
    }

    private static void ResetContinuity()
    {
        trackedWindow = null;
        trackedClip = null;
        continuousTicks = 0;
    }

    // A janela avança pelo tempo real e volta a minTime no loop; qualquer outro salto é seek.
    private static bool IsRunningContinuously(AnimationWindow window)
    {
        var now = EditorApplication.timeSinceStartup;
        var time = window.time;
        var clip = window.animationClip;
        if (trackedWindow != window || trackedClip != clip)
        {
            trackedWindow = window;
            trackedClip = clip;
            lastTime = time;
            lastAdvance = now;
            continuousTicks = 0;
            return false;
        }

        var delta = time - lastTime;
        if (Mathf.Approximately(delta, 0f))
        {
            return continuousTicks >= RequiredContinuousTicks;
        }

        var elapsed = (float)(now - lastAdvance);
        var advancing = Mathf.Abs(delta - elapsed) <= ContinuityTolerance;
        var looped = delta < 0f && time - AnimationWindowBridge.GetMinTime(window) <= elapsed + ContinuityTolerance;
        lastTime = time;
        lastAdvance = now;
        continuousTicks = advancing || looped ? continuousTicks + 1 : 0;
        return continuousTicks >= RequiredContinuousTicks;
    }

    private static void Synchronize(AnimationAudioModel model, float time)
    {
        var used = new HashSet<string>();
        foreach (var track in model.Tracks)
        {
            if (!track.Play || !track.TryGetPosition(time, out var position))
            {
                continue;
            }

            used.Add(track.Key);
            SynchronizeVoice(track, position);
        }

        foreach (var pair in Voices)
        {
            if (!used.Contains(pair.Key) && pair.Value != null && pair.Value.isPlaying)
            {
                pair.Value.Stop();
            }
        }
    }

    private static void SynchronizeVoice(AnimationAudioTrack track, float position)
    {
        var voice = GetVoice(track.Key);
        if (voice.clip != track.Clip)
        {
            voice.Stop();
            voice.clip = track.Clip;
        }

        var mix = AnimationAudioFalloff.GetMix(track);
        voice.volume = track.Volume * mix.Volume;
        voice.panStereo = mix.Pan;
        voice.pitch = track.PlaybackRate;
        voice.loop = track.Loop;
        voice.outputAudioMixerGroup = track.Mixer;

        if (track.Clip.loadState != AudioDataLoadState.Loaded)
        {
            track.Clip.LoadAudioData();
            return;
        }

        if (!track.Loop && position >= track.Clip.length - EndMargin)
        {
            return;
        }

        if (!voice.isPlaying)
        {
            voice.time = position;
            voice.Play();
        }
        else if (Mathf.Abs(voice.time - position) > ResyncThreshold)
        {
            voice.time = position;
        }
    }

    private static AudioSource GetVoice(string key)
    {
        if (Voices.TryGetValue(key, out var voice) && voice != null)
        {
            return voice;
        }

        var gameObject = EditorUtility.CreateGameObjectWithHideFlags("AnimationAudioVoice", HideFlags.HideAndDontSave, typeof(AudioSource));
        voice = gameObject.GetComponent<AudioSource>();
        voice.playOnAwake = false;
        voice.spatialBlend = 0f;
        Voices[key] = voice;
        return voice;
    }

    private static void StopVoices()
    {
        foreach (var voice in Voices.Values)
        {
            if (voice != null && voice.isPlaying)
            {
                voice.Stop();
            }
        }
    }

    private static void DestroyVoices()
    {
        foreach (var voice in Voices.Values)
        {
            if (voice != null)
            {
                voice.Stop();
                UnityEngine.Object.DestroyImmediate(voice.gameObject);
            }
        }
        Voices.Clear();
    }
}

// ---------------------------------------------------------------- Painel

internal static class AnimationAudioPanel
{
    public const float ScrollbarWidth = 13f;
    public const float HeaderHeight = 22f;
    public const float SplitterHeight = 4f;
    public const float MinimumHeight = 70f;

    private const float MinimumAnimationHeight = 110f;

    private const float LaneHeight = 42f;
    private const float WheelSpeed = 16f;
    private const int MaxTextureWidth = 8192;

    public static readonly Color[] Palette =
    {
        new Color(0.95f, 0.28f, 0.25f),
        new Color(0.25f, 0.70f, 0.95f),
        new Color(0.40f, 0.85f, 0.45f),
        new Color(0.97f, 0.73f, 0.25f),
        new Color(0.75f, 0.50f, 0.95f),
        new Color(0.95f, 0.50f, 0.75f),
    };

    private static readonly Color PanelColor = new Color(0.19f, 0.19f, 0.19f);
    private static readonly Color LaneColor = new Color(0.15f, 0.15f, 0.15f);
    private static readonly Color LineColor = new Color(0.09f, 0.09f, 0.09f);
    private static readonly Color SplitterColor = new Color(0.1f, 0.1f, 0.1f);
    private static readonly Color PlayheadColor = new Color(1f, 1f, 1f, 0.9f);
    private static readonly Dictionary<string, BakedLane> Lanes = new Dictionary<string, BakedLane>();

    private static GUIStyle markerStyle;
    private static float laneScroll;
    private static bool resizing;
    private static AnimationAudioTrack draggedTrack;
    private static float dragTimeOffset;
    private static bool scrubbing;

    private sealed class BakedLane
    {
        public Texture2D Texture;
        public Color32[] Pixels;
        public int Width;
        public int Height;
        public float ShownStart;
        public float ShownWidth;
        public string Signature;
        public bool HasPeaks;
    }

    public static float GetReservedHeight(float windowHeight)
    {
        var settings = AnimationAudioSettings.Instance;
        var available = windowHeight - MinimumAnimationHeight;
        if (settings.collapsed || available < MinimumHeight)
        {
            return HeaderHeight + SplitterHeight;
        }
        return Mathf.Clamp(settings.panelHeight, MinimumHeight, available);
    }

    public static void Draw(Rect rect, AnimationWindow window, float timeLeft, float timeWidth, bool resizable)
    {
        var settings = AnimationAudioSettings.Instance;
        EditorGUI.DrawRect(rect, PanelColor);

        var model = AnimationAudioModel.For(window);
        if (model == null)
        {
            GUI.Label(rect, "Selecione um clip na janela Animation.", EditorStyles.centeredGreyMiniLabel);
            return;
        }

        var y = rect.y;
        if (resizable)
        {
            DrawSplitter(new Rect(rect.x, y, rect.width, SplitterHeight), window);
            y += SplitterHeight;
        }

        DrawHeader(new Rect(rect.x, y, rect.width, HeaderHeight), model, window);
        y += HeaderHeight;

        if (!settings.collapsed && rect.yMax - y > 1f)
        {
            DrawLanes(new Rect(rect.x, y, rect.width, rect.yMax - y), model, window, timeLeft, timeWidth);
        }
    }

    private static void DrawSplitter(Rect rect, AnimationWindow window)
    {
        var settings = AnimationAudioSettings.Instance;
        EditorGUIUtility.AddCursorRect(rect, MouseCursor.ResizeVertical);
        var controlId = GUIUtility.GetControlID(FocusType.Passive);
        var currentEvent = Event.current;
        if (currentEvent.type == EventType.MouseDown && rect.Contains(currentEvent.mousePosition))
        {
            resizing = true;
            GUIUtility.hotControl = controlId;
            settings.panelHeight = GetReservedHeight(window.position.height);
            currentEvent.Use();
        }
        else if (currentEvent.type == EventType.MouseDrag && resizing && GUIUtility.hotControl == controlId)
        {
            settings.panelHeight = Mathf.Clamp(settings.panelHeight - currentEvent.delta.y, MinimumHeight, Mathf.Max(MinimumHeight, window.position.height - MinimumAnimationHeight));
            currentEvent.Use();
            window.Repaint();
        }
        else if (currentEvent.type == EventType.MouseUp && resizing && GUIUtility.hotControl == controlId)
        {
            resizing = false;
            GUIUtility.hotControl = 0;
            settings.Save();
            currentEvent.Use();
        }
        EditorGUI.DrawRect(rect, SplitterColor);
    }

    private static void DrawHeader(Rect rect, AnimationAudioModel model, AnimationWindow window)
    {
        var settings = AnimationAudioSettings.Instance;
        EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - 1f, rect.width, 1f), LineColor);

        var row = new Rect(rect.x + 4f, rect.y + 2f, 90f, 18f);
        EditorGUI.BeginChangeCheck();
        var expanded = EditorGUI.Foldout(new Rect(row.x + 10f, row.y, row.width - 10f, row.height), !settings.collapsed, "Áudio", true);
        if (EditorGUI.EndChangeCheck())
        {
            settings.collapsed = !expanded;
            settings.Save();
            window.Repaint();
            GUIUtility.ExitGUI();
        }

        row.x = row.xMax;
        row.width = 150f;
        EditorGUI.BeginChangeCheck();
        var playback = EditorGUI.ToggleLeft(row, "Tocar com a animação", settings.playbackEnabled);
        if (EditorGUI.EndChangeCheck())
        {
            settings.playbackEnabled = playback;
            settings.Save();
        }

        row.x = row.xMax;
        row.width = 120f;
        EditorGUI.BeginChangeCheck();
        var falloff = EditorGUI.ToggleLeft(row, "Áudio 3D (câmera)", settings.falloffEnabled);
        if (EditorGUI.EndChangeCheck())
        {
            settings.falloffEnabled = falloff;
            settings.Save();
        }

        row.x = row.xMax + 8f;
        row.width = 100f;
        GUI.Label(row, "Adicionar áudio");
        row.x = row.xMax + 4f;
        row.width = Mathf.Clamp(rect.xMax - row.x - 6f, 60f, 240f);
        var added = EditorGUI.ObjectField(row, GUIContent.none, null, typeof(AudioClip), false) as AudioClip;
        if (added != null)
        {
            model.AddManualTrack(added);
            window.Repaint();
        }
    }

    private static void DrawLanes(Rect rect, AnimationAudioModel model, AnimationWindow window, float timeLeft, float timeWidth)
    {
        var tracks = model.Tracks;
        var contentHeight = tracks.Count * LaneHeight;
        var maxScroll = Mathf.Max(0f, contentHeight - rect.height);
        laneScroll = Mathf.Clamp(laneScroll, 0f, maxScroll);

        var currentEvent = Event.current;
        if (currentEvent.type == EventType.ScrollWheel && rect.Contains(currentEvent.mousePosition))
        {
            laneScroll = Mathf.Clamp(laneScroll + currentEvent.delta.y * WheelSpeed, 0f, maxScroll);
            currentEvent.Use();
            window.Repaint();
        }

        if (maxScroll > 0f)
        {
            var barRect = new Rect(rect.xMax - ScrollbarWidth, rect.y, ScrollbarWidth, rect.height);
            laneScroll = GUI.VerticalScrollbar(barRect, laneScroll, rect.height, 0f, contentHeight);
        }

        var dragControl = GUIUtility.GetControlID(FocusType.Passive);
        var shownArea = AnimationWindowBridge.GetShownArea(window);
        var frameRate = AnimationWindowBridge.GetFrameRate(window);

        GUI.BeginGroup(rect);
        var left = timeLeft - rect.x;
        if (tracks.Count == 0)
        {
            var message = model.Root == null
                ? "Sem objeto raiz selecionado. Adicione um áudio acima."
                : "Nenhum áudio com toggle neste clip. Adicione um áudio acima.";
            GUI.Label(new Rect(0f, 4f, rect.width, 20f), message, EditorStyles.centeredGreyMiniLabel);
        }

        // A remoção altera a lista: adiar até o fim do desenho.
        AnimationAudioTrack removed = null;
        for (var index = 0; index < tracks.Count; index++)
        {
            var laneY = index * LaneHeight - laneScroll;
            if (laneY + LaneHeight < 0f || laneY > rect.height)
            {
                continue;
            }

            var track = tracks[index];
            var controls = new Rect(0f, laneY, left, LaneHeight);
            var timeline = new Rect(left, laneY, timeWidth, LaneHeight - 1f);
            if (DrawLaneControls(controls, model, track, frameRate))
            {
                removed = track;
            }
            DrawTimeline(timeline, track, shownArea, model.Signature, frameRate);
            HandleTimelineInput(timeline, model, window, track, shownArea, frameRate, dragControl);
        }

        DrawPlayhead(new Rect(left, 0f, timeWidth, rect.height), window.time, shownArea);
        EditorGUI.DrawRect(new Rect(left - 1f, 0f, 1f, rect.height), LineColor);
        GUI.EndGroup();

        if (currentEvent.rawType == EventType.MouseUp && GUIUtility.hotControl == dragControl)
        {
            FinishDrag(model);
        }

        if (removed != null)
        {
            model.RemoveManualTrack(removed);
            window.Repaint();
            GUIUtility.ExitGUI();
        }
    }

    private static bool DrawLaneControls(Rect rect, AnimationAudioModel model, AnimationAudioTrack track, float frameRate)
    {
        EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - 1f, rect.width, 1f), LineColor);

        EditorGUI.BeginChangeCheck();
        var color = EditorGUI.ColorField(new Rect(rect.x + 4f, rect.y + 4f, 14f, 14f), GUIContent.none, track.Color, false, false, false);
        if (EditorGUI.EndChangeCheck())
        {
            model.SetColor(track, color);
        }

        var removeWidth = track.IsManual ? 18f : 0f;
        GUI.Label(
            new Rect(rect.x + 22f, rect.y + 2f, rect.width - 26f - removeWidth, 18f),
            new GUIContent(track.Label, track.Clip.name),
            EditorStyles.miniLabel);

        var removed = track.IsManual && GUI.Button(new Rect(rect.xMax - 20f, rect.y + 3f, 16f, 16f), "×", EditorStyles.miniButton);

        var row = new Rect(rect.x + 4f, rect.y + 21f, 46f, 16f);
        EditorGUI.BeginChangeCheck();
        var visible = EditorGUI.ToggleLeft(row, "Ver", track.Visible, EditorStyles.miniLabel);
        if (EditorGUI.EndChangeCheck())
        {
            model.SetVisible(track, visible);
        }

        row.x = row.xMax;
        row.width = 52f;
        EditorGUI.BeginChangeCheck();
        var play = EditorGUI.ToggleLeft(row, "Tocar", track.Play, EditorStyles.miniLabel);
        if (EditorGUI.EndChangeCheck())
        {
            model.SetPlay(track, play);
        }

        row.x = row.xMax;
        row.width = Mathf.Max(0f, rect.xMax - row.x - 4f);
        if (track.IsManual)
        {
            EditorGUI.BeginChangeCheck();
            var offset = EditorGUI.FloatField(row, track.Manual.offset, EditorStyles.miniTextField);
            if (EditorGUI.EndChangeCheck())
            {
                model.SetOffset(track, offset, true);
            }
        }
        else if (track.Segments.Count > 0)
        {
            GUI.Label(row, FormatActivation(track.Segments[0].Start, frameRate), EditorStyles.miniLabel);
        }

        return removed;
    }

    private static string FormatActivation(float time, float frameRate)
    {
        var frame = Mathf.RoundToInt(time * frameRate);
        var rate = Mathf.Max(1, Mathf.RoundToInt(frameRate));
        return (frame / rate) + ":" + (frame % rate).ToString("00") + " · frame " + frame;
    }

    private static float TimeToX(Rect timeline, Rect shownArea, float time)
    {
        return timeline.x + (time - shownArea.xMin) / shownArea.width * timeline.width;
    }

    private static float XToTime(Rect timeline, Rect shownArea, float x)
    {
        return shownArea.xMin + (x - timeline.x) / timeline.width * shownArea.width;
    }

    private static void DrawTimeline(Rect rect, AnimationAudioTrack track, Rect shownArea, string signature, float frameRate)
    {
        if (Event.current.type != EventType.Repaint)
        {
            return;
        }

        EditorGUI.DrawRect(rect, LaneColor);
        if (!track.Visible || rect.width < 1f)
        {
            return;
        }

        var color = track.Color;
        var lane = Bake(track, rect, shownArea, signature);
        GUI.DrawTexture(rect, lane.Texture, ScaleMode.StretchToFill, true, 0f, color, 0f, 0f);

        if (markerStyle == null)
        {
            markerStyle = new GUIStyle(EditorStyles.miniLabel) { padding = new RectOffset(3, 0, 0, 0) };
            markerStyle.normal.textColor = Color.white;
        }

        foreach (var segment in track.Segments)
        {
            var x = TimeToX(rect, shownArea, segment.Start);
            if (x < rect.x || x > rect.xMax)
            {
                continue;
            }

            var marker = new Color(color.r, color.g, color.b, 1f);
            EditorGUI.DrawRect(new Rect(x, rect.y, 1f, rect.height), Color.Lerp(marker, Color.white, 0.6f));
            GUI.Label(new Rect(x, rect.y, 120f, 14f), FormatActivation(segment.Start, frameRate), markerStyle);
        }
    }

    private static BakedLane Bake(AnimationAudioTrack track, Rect rect, Rect shownArea, string signature)
    {
        var scale = EditorGUIUtility.pixelsPerPoint;
        var width = Mathf.Clamp(Mathf.RoundToInt(rect.width * scale), 1, MaxTextureWidth);
        var height = Mathf.Max(1, Mathf.RoundToInt(rect.height * scale));
        var peaks = AnimationAudioPeaks.Get(track.Clip);
        var hasPeaks = peaks != null;

        if (!Lanes.TryGetValue(track.Key, out var lane))
        {
            lane = new BakedLane();
            Lanes[track.Key] = lane;
        }

        if (lane.Texture != null
            && lane.Width == width
            && lane.Height == height
            && Mathf.Approximately(lane.ShownStart, shownArea.xMin)
            && Mathf.Approximately(lane.ShownWidth, shownArea.width)
            && lane.Signature == signature
            && lane.HasPeaks == hasPeaks)
        {
            return lane;
        }

        if (lane.Texture == null || lane.Width != width || lane.Height != height)
        {
            if (lane.Texture != null)
            {
                UnityEngine.Object.DestroyImmediate(lane.Texture);
            }
            lane.Texture = new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            lane.Pixels = new Color32[width * height];
        }

        lane.Width = width;
        lane.Height = height;
        lane.ShownStart = shownArea.xMin;
        lane.ShownWidth = shownArea.width;
        lane.Signature = signature;
        lane.HasPeaks = hasPeaks;

        Array.Clear(lane.Pixels, 0, lane.Pixels.Length);
        var secondsPerPixel = shownArea.width / width;
        for (var column = 0; column < width; column++)
        {
            var from = shownArea.xMin + column * secondsPerPixel;
            var to = from + secondsPerPixel;
            foreach (var segment in track.Segments)
            {
                if (to > segment.Start && from < segment.End)
                {
                    FillColumn(lane, column, track, peaks, segment, Mathf.Max(from, segment.Start), Mathf.Min(to, segment.End));
                }
            }
        }

        lane.Texture.SetPixels32(lane.Pixels);
        lane.Texture.Apply(false);
        return lane;
    }

    private static void FillColumn(BakedLane lane, int column, AnimationAudioTrack track, AnimationAudioPeaks peaks, AnimationAudioSegment segment, float from, float to)
    {
        var alpha = (byte)235;
        var low = -0.3f;
        var high = 0.3f;
        if (peaks != null && peaks.HasData)
        {
            var duration = Mathf.Max(0.0001f, track.Duration);
            var start = (from - segment.Start) / duration;
            var end = (to - segment.Start) / duration;
            if (track.Loop && end > 1f)
            {
                var whole = Mathf.Floor(start);
                start -= whole;
                end -= whole;
            }
            peaks.GetRange(start, Mathf.Min(end, 1f), out low, out high);
            low *= peaks.Gain;
            high *= peaks.Gain;
        }
        else
        {
            alpha = 110;
        }

        var middle = (lane.Height - 1) * 0.5f;
        var first = Mathf.Clamp(Mathf.FloorToInt(middle + low * middle), 0, lane.Height - 1);
        var last = Mathf.Clamp(Mathf.CeilToInt(middle + high * middle), first, lane.Height - 1);
        var pixel = new Color32(255, 255, 255, alpha);
        for (var row = first; row <= last; row++)
        {
            lane.Pixels[row * lane.Width + column] = pixel;
        }
    }

    private static void HandleTimelineInput(Rect rect, AnimationAudioModel model, AnimationWindow window, AnimationAudioTrack track, Rect shownArea, float frameRate, int controlId)
    {
        var currentEvent = Event.current;
        var isHot = GUIUtility.hotControl == controlId;
        if (currentEvent.type == EventType.MouseDown && currentEvent.button == 0 && rect.Contains(currentEvent.mousePosition))
        {
            GUIUtility.hotControl = controlId;
            var time = XToTime(rect, shownArea, currentEvent.mousePosition.x);
            if (track.IsManual && track.TryGetPosition(time, out _))
            {
                draggedTrack = track;
                dragTimeOffset = time - track.Manual.offset;
            }
            else
            {
                scrubbing = true;
                ScrubTo(window, time, frameRate);
            }
            currentEvent.Use();
        }
        else if (currentEvent.type == EventType.MouseDrag && isHot)
        {
            var time = XToTime(rect, shownArea, currentEvent.mousePosition.x);
            if (draggedTrack == track)
            {
                model.SetOffset(track, Snap(time - dragTimeOffset, frameRate), false);
                window.Repaint();
                currentEvent.Use();
            }
            else if (scrubbing)
            {
                ScrubTo(window, time, frameRate);
                currentEvent.Use();
            }
        }
    }

    private static void FinishDrag(AnimationAudioModel model)
    {
        if (draggedTrack != null)
        {
            model.SaveTrack(draggedTrack);
            AnimationAudioModel.MarkDirty();
        }
        draggedTrack = null;
        scrubbing = false;
        GUIUtility.hotControl = 0;
    }

    private static float Snap(float time, float frameRate)
    {
        return Mathf.Max(0f, Mathf.Round(time * frameRate) / frameRate);
    }

    private static void ScrubTo(AnimationWindow window, float time, float frameRate)
    {
        window.time = Snap(time, frameRate);
        window.Repaint();
    }

    private static void DrawPlayhead(Rect rect, float time, Rect shownArea)
    {
        var x = TimeToX(rect, shownArea, time);
        if (x >= rect.x && x <= rect.xMax)
        {
            EditorGUI.DrawRect(new Rect(x, rect.y, 1f, rect.height), PlayheadColor);
        }
    }
}

// ---------------------------------------------------------------- Harmony

// Por reflexão: o VRChat SDK já traz o Harmony, e uma DLL própria poderia gerar assembly duplicado.
[InitializeOnLoad]
internal static class AnimationAudioHarmonyPatch
{
    private const string HarmonyId = "shaderview.animation-audio-waveform";
    private const float MinimumWindowHeight = 140f;
    private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;

    private static readonly Dictionary<int, float> ReservedHeights = new Dictionary<int, float>();

    private static object harmony;
    private static bool panelFailed;

    public static bool Active { get; private set; }

    static AnimationAudioHarmonyPatch()
    {
        EditorApplication.hierarchyWindowItemOnGUI += HandleHierarchyGui;
        EditorApplication.projectWindowItemOnGUI += HandleProjectGui;
        SceneView.duringSceneGui += HandleSceneGui;
        AssemblyReloadEvents.beforeAssemblyReload += Remove;
    }

    private static void HandleHierarchyGui(int instanceId, Rect rect)
    {
        ApplyInsideGui();
    }

    private static void HandleProjectGui(string guid, Rect rect)
    {
        ApplyInsideGui();
    }

    private static void HandleSceneGui(SceneView sceneView)
    {
        ApplyInsideGui();
    }

    // Fazer o patch compila o método e inicializa AnimationWindowStyles, que exige um skin de GUI ativo.
    // Fora de OnGUI o inicializador falha e a janela Animation fica inutilizável até o próximo reload.
    private static void ApplyInsideGui()
    {
        EditorApplication.hierarchyWindowItemOnGUI -= HandleHierarchyGui;
        EditorApplication.projectWindowItemOnGUI -= HandleProjectGui;
        SceneView.duringSceneGui -= HandleSceneGui;
        Apply();
        if (Active)
        {
            AnimationWindowBridge.RepaintAll();
        }
    }

    private static void Apply()
    {
        var harmonyType = AnimationWindowBridge.FindType("HarmonyLib.Harmony");
        var methodType = AnimationWindowBridge.FindType("HarmonyLib.HarmonyMethod");
        var target = AnimationWindowBridge.EditorGuiMethod;
        if (harmonyType == null || methodType == null || target == null)
        {
            return;
        }

        try
        {
            var methodConstructor = methodType.GetConstructor(new[] { typeof(MethodInfo) });
            var prefix = methodConstructor.Invoke(new object[] { typeof(AnimationAudioHarmonyPatch).GetMethod(nameof(Prefix), PrivateStatic) });
            var finalizer = methodConstructor.Invoke(new object[] { typeof(AnimationAudioHarmonyPatch).GetMethod(nameof(Finalizer), PrivateStatic) });

            harmony = Activator.CreateInstance(harmonyType, HarmonyId);
            foreach (var method in harmonyType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (method.Name != "Patch")
                {
                    continue;
                }

                var parameters = method.GetParameters();
                var arguments = new object[parameters.Length];
                for (var index = 0; index < parameters.Length; index++)
                {
                    switch (parameters[index].Name)
                    {
                        case "original":
                            arguments[index] = target;
                            break;
                        case "prefix":
                            arguments[index] = prefix;
                            break;
                        case "finalizer":
                            arguments[index] = finalizer;
                            break;
                    }
                }

                method.Invoke(harmony, arguments);
                Active = true;
                break;
            }
        }
        catch (Exception exception)
        {
            Active = false;
            Debug.LogWarning("Animation Audio Waveform: patch indisponível, use a janela separada. " + exception);
        }
    }

    private static void Remove()
    {
        if (harmony == null)
        {
            return;
        }

        try
        {
            var unpatch = harmony.GetType().GetMethod("UnpatchAll", new[] { typeof(string) });
            unpatch?.Invoke(harmony, new object[] { HarmonyId });
        }
        catch (Exception exception)
        {
            Debug.LogWarning("Animation Audio Waveform: falha ao remover o patch. " + exception.Message);
        }
        harmony = null;
        Active = false;
    }

    private static float GetReservedHeight(AnimationWindow window, float windowHeight)
    {
        var enabled = !panelFailed
            && AnimationAudioSettings.Instance.panelEnabled
            && window.animationClip != null
            && windowHeight >= MinimumWindowHeight;
        return enabled ? AnimationAudioPanel.GetReservedHeight(windowHeight) : 0f;
    }

    // Reservar por BeginArea funciona porque o AnimEditor distribui seu conteúdo com GUILayout.
    private static void Prefix(EditorWindow parent, ref Rect position, out float __state)
    {
        __state = 0f;
        try
        {
            if (!(parent is AnimationWindow window))
            {
                return;
            }

            // A altura só muda no Layout para que Layout e Repaint abram os mesmos grupos.
            var id = window.GetInstanceID();
            if (Event.current.type == EventType.Layout)
            {
                ReservedHeights[id] = GetReservedHeight(window, position.height);
            }

            if (ReservedHeights.TryGetValue(id, out var height) && height > 0f)
            {
                position.height -= height;
                GUILayout.BeginArea(new Rect(0f, 0f, position.width, position.height));
                __state = height;
            }
        }
        catch (Exception exception)
        {
            DisablePanel(exception);
        }
    }

    private static Exception Finalizer(Exception __exception, EditorWindow parent, float __state)
    {
        if (__state <= 0f)
        {
            return __exception;
        }

        try
        {
            GUILayout.EndArea();
            if (__exception != null || Event.current.type == EventType.Used || !(parent is AnimationWindow window))
            {
                return __exception;
            }

            var size = window.position.size;
            var hierarchyWidth = AnimationWindowBridge.GetHierarchyWidth(window);
            var contentWidth = AnimationWindowBridge.GetContentWidth(window);
            var rect = new Rect(0f, size.y - __state, size.x, __state);
            AnimationAudioPanel.Draw(rect, window, hierarchyWidth, contentWidth - AnimationAudioPanel.ScrollbarWidth, true);
        }
        catch (ExitGUIException exception)
        {
            return exception;
        }
        catch (Exception exception)
        {
            DisablePanel(exception);
        }
        return __exception;
    }

    // Um erro do painel nunca pode impedir o uso da janela Animation.
    private static void DisablePanel(Exception exception)
    {
        panelFailed = true;
        Debug.LogError("Animation Audio Waveform: painel desativado até a próxima recompilação. " + exception);
    }
}

// ---------------------------------------------------------------- Validação

public static class AnimationAudioValidation
{
    private const double AudioCheckDelay = 0.4;

    private static readonly List<string> Failures = new List<string>();

    private static GameObject root;
    private static AudioClip tone;
    private static AudioSource[] voices;
    private static double audioCheckAt;

    [MenuItem("Tools/Dosker Tools/Validar Animation Audio")]
    public static void RunAutomatedValidation()
    {
        Failures.Clear();
        foreach (var member in AnimationWindowBridge.MissingMembers())
        {
            Failures.Add("API interna ausente: " + member);
        }

        Debug.Log("ANIMATION_AUDIO_HARMONY_ACTIVE=" + AnimationAudioHarmonyPatch.Active);

        tone = CreateTone();
        ValidatePeaks();
        ValidateActivation();
        ValidateContinuityRules();
        ValidateFalloff();

        if (Application.isBatchMode)
        {
            Finish();
            return;
        }

        // Duas fontes ao mesmo tempo, fora do Play mode, são a base da reprodução.
        voices = new[] { CreateVoice(), CreateVoice() };
        foreach (var voice in voices)
        {
            voice.Play();
        }
        audioCheckAt = EditorApplication.timeSinceStartup + AudioCheckDelay;
        EditorApplication.update += CheckAudio;
    }

    private static AudioClip CreateTone()
    {
        const int frequency = 44100;
        var clip = AudioClip.Create("AnimationAudioValidationTone", frequency, 1, frequency, false);
        var samples = new float[frequency];
        for (var index = 0; index < samples.Length; index++)
        {
            samples[index] = 0.5f * Mathf.Sin(index * 0.05f);
        }
        clip.SetData(samples, 0);
        clip.hideFlags = HideFlags.HideAndDontSave;
        return clip;
    }

    private static AudioSource CreateVoice()
    {
        var gameObject = EditorUtility.CreateGameObjectWithHideFlags("AnimationAudioValidationVoice", HideFlags.HideAndDontSave, typeof(AudioSource));
        var voice = gameObject.GetComponent<AudioSource>();
        voice.playOnAwake = false;
        voice.spatialBlend = 0f;
        voice.volume = 0f;
        voice.loop = true;
        voice.clip = tone;
        return voice;
    }

    private static void ValidatePeaks()
    {
        var peaks = AnimationAudioPeaks.Get(tone);
        if (peaks == null || !peaks.HasData)
        {
            Failures.Add("Waveform sem dados para o clip de teste.");
        }
    }

    private static void ValidateContinuityRules()
    {
        var track = new AnimationAudioTrack { Clip = tone };
        track.Segments.Add(new AnimationAudioSegment { Start = 2f, End = 3f });
        if (track.TryGetPosition(1.99f, out _))
        {
            Failures.Add("Áudio tocaria antes da ativação.");
        }
        if (!track.TryGetPosition(2.25f, out var position) || !Mathf.Approximately(position, 0.25f))
        {
            Failures.Add("Posição do áudio não parte do ponto de ativação.");
        }
    }

    private static void ValidateFalloff()
    {
        const float minDistance = 2f;
        const float maxDistance = 8f;

        var gameObject = EditorUtility.CreateGameObjectWithHideFlags("AnimationAudioValidationFalloff", HideFlags.HideAndDontSave, typeof(AudioSource));
        try
        {
            var source = gameObject.GetComponent<AudioSource>();
            source.minDistance = minDistance;
            source.maxDistance = maxDistance;
            source.spatialBlend = 1f;

            var scale = AnimationAudioFalloff.RolloffScale;
            source.rolloffMode = AudioRolloffMode.Logarithmic;
            ExpectGain(source, 1f, 1f, "logarítmico antes do mínimo");
            ExpectGain(source, 4f, minDistance / (2f * scale + minDistance), "logarítmico entre mínimo e máximo");
            ExpectGain(source, 16f, minDistance / (14f * scale + minDistance), "logarítmico além do máximo");

            source.rolloffMode = AudioRolloffMode.Linear;
            ExpectGain(source, 1f, 1f, "linear antes do mínimo");
            ExpectGain(source, 5f, 0.5f, "linear entre mínimo e máximo");
            ExpectGain(source, 16f, 0f, "linear além do máximo");

            source.rolloffMode = AudioRolloffMode.Custom;
            source.SetCustomCurve(AudioSourceCurveType.CustomRolloff, AnimationCurve.Linear(0f, 1f, 1f, 0f));
            ExpectGain(source, 2f, 0.75f, "customizado antes do mínimo");
            ExpectGain(source, 4f, 0.5f, "customizado entre mínimo e máximo");
            ExpectGain(source, 16f, 0f, "customizado além do máximo");

            source.rolloffMode = AudioRolloffMode.Linear;
            source.spatialBlend = 0f;
            ExpectGain(source, 16f, 1f, "fonte 2D");
            source.spatialBlend = 0.5f;
            ExpectGain(source, 16f, 0.5f, "fonte com spatial blend parcial");

            ValidateSpatialMix(source);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(gameObject);
        }
    }

    // Os valores esperados de cada canal foram medidos na saída real de AudioSources 3D.
    private static void ValidateSpatialMix(AudioSource source)
    {
        source.minDistance = 100f;
        source.maxDistance = 500f;
        source.spatialBlend = 1f;
        ExpectChannels(source, new Vector3(0f, 0f, 1f), 0.7071f, 0.7071f, "som à frente");
        ExpectChannels(source, new Vector3(0f, 0f, -1f), 0.7071f, 0.7071f, "som atrás");
        ExpectChannels(source, new Vector3(0f, 1f, 0f), 0.7071f, 0.7071f, "som acima");
        ExpectChannels(source, new Vector3(1f, 0f, 0f), 0f, 1f, "som à direita");
        ExpectChannels(source, new Vector3(-1f, 0f, 0f), 1f, 0f, "som à esquerda");
        ExpectChannels(source, new Vector3(0.7071f, 0f, 0.7071f), 0.2588f, 0.9659f, "som a 45 graus");
        ExpectChannels(source, new Vector3(0.5f, 0f, 0.866f), 0.4114f, 0.9115f, "som a 30 graus");
        ExpectChannels(source, new Vector3(1f, 1f, 1f).normalized, 0.3569f, 0.9342f, "som na diagonal");
        ExpectChannels(source, Vector3.zero, 0.7071f, 0.7071f, "som na posição da câmera");

        source.spatialBlend = 0.5f;
        ExpectChannels(source, new Vector3(1f, 0f, 0f), 0.3536f, 0.8536f, "spatial blend parcial à direita");
        source.panStereo = 0.5f;
        ExpectChannels(source, new Vector3(0f, 0f, 1f), 0.6034f, 0.7864f, "spatial blend parcial com pan");
        source.panStereo = 0f;

        source.spatialBlend = 0f;
        var flat = AnimationAudioFalloff.GetMix(source, Vector3.left, Vector3.right, 2);
        if (!Mathf.Approximately(flat.Volume, 1f) || !Mathf.Approximately(flat.Pan, 0f))
        {
            Failures.Add("Clip estéreo em fonte 2D mudou de volume ou pan.");
        }

        source.spatialBlend = 1f;
        var side = AnimationAudioFalloff.GetMix(source, Vector3.left, Vector3.right, 2);
        if (!Mathf.Approximately(side.Volume, 1f) || !Mathf.Approximately(side.Pan, 1f))
        {
            Failures.Add("Clip estéreo à direita não foi para o canal direito.");
        }
    }

    private static void ExpectChannels(AudioSource source, Vector3 direction, float expectedLeft, float expectedRight, string label)
    {
        var mix = AnimationAudioFalloff.GetMix(source, source.transform.position - direction, Vector3.right, 1);
        var left = mix.Volume * Mathf.Sqrt((1f - mix.Pan) * 0.5f);
        var right = mix.Volume * Mathf.Sqrt((1f + mix.Pan) * 0.5f);
        if (Mathf.Abs(left - expectedLeft) > 0.002f || Mathf.Abs(right - expectedRight) > 0.002f)
        {
            Failures.Add("Pan incorreto (" + label + "): " + left + " / " + right + " em vez de " + expectedLeft + " / " + expectedRight);
        }
    }

    private static void ExpectGain(AudioSource source, float distance, float expected, string label)
    {
        var gain = AnimationAudioFalloff.GetGain(source, source.transform.position + Vector3.forward * distance);
        if (Mathf.Abs(gain - expected) > 0.001f)
        {
            Failures.Add("Falloff incorreto (" + label + "): " + gain + " em vez de " + expected);
        }
    }

    private static void ValidateActivation()
    {
        if (AnimationMode.InAnimationMode())
        {
            Debug.Log("ANIMATION_AUDIO_ACTIVATION_SKIPPED: feche o preview da janela Animation para validar o ponto de ativação.");
            return;
        }

        const float activation = 0.5137f;
        const float deactivation = 0.9f;

        root = EditorUtility.CreateGameObjectWithHideFlags("AnimationAudioValidationRoot", HideFlags.HideAndDontSave, typeof(Animator));
        var parent = EditorUtility.CreateGameObjectWithHideFlags("Parent", HideFlags.HideAndDontSave);
        var child = EditorUtility.CreateGameObjectWithHideFlags("Child", HideFlags.HideAndDontSave, typeof(AudioSource));
        var threshold = EditorUtility.CreateGameObjectWithHideFlags("Threshold", HideFlags.HideAndDontSave);
        parent.transform.SetParent(root.transform);
        child.transform.SetParent(parent.transform);
        threshold.transform.SetParent(root.transform);
        var source = child.GetComponent<AudioSource>();
        source.clip = tone;
        source.playOnAwake = true;

        var clip = new AnimationClip { frameRate = 60f, hideFlags = HideFlags.HideAndDontSave };
        var curve = new AnimationCurve(new Keyframe(0f, 0f), new Keyframe(activation, 1f), new Keyframe(deactivation, 0f));
        var thresholdCurve = new AnimationCurve(
            new Keyframe(0f, 0.001f),
            new Keyframe(1f, 0f),
            new Keyframe(2f, -0.5f),
            new Keyframe(3f, 0f),
            new Keyframe(4f, 0.25f),
            new Keyframe(5f, 0f),
            new Keyframe(6f, 0.002f),
            new Keyframe(7f, 0f),
            new Keyframe(8f, 1f));
        MakeStepped(curve);
        MakeStepped(thresholdCurve);
        AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Parent", typeof(GameObject), "m_IsActive"), curve);
        AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Threshold", typeof(GameObject), "m_IsActive"), thresholdCurve);

        var model = AnimationAudioModel.Create(clip, root);
        if (model.Tracks.Count != 1 || model.Tracks[0].Segments.Count != 1)
        {
            Failures.Add("Faixa de toggle no pai não detectada.");
        }
        else
        {
            var segment = model.Tracks[0].Segments[0];
            if (!Mathf.Approximately(segment.Start, activation) || !Mathf.Approximately(segment.End, deactivation))
            {
                Failures.Add("Trecho calculado incorreto: " + segment.Start + " a " + segment.End);
            }
        }

        AnimationMode.StartAnimationMode();
        try
        {
            if (Sample(clip, activation - 0.0005f, child))
            {
                Failures.Add("Objeto já ativo antes do ponto calculado.");
            }
            if (!Sample(clip, activation, child))
            {
                Failures.Add("Objeto inativo no ponto calculado.");
            }
            if (Sample(clip, deactivation, child))
            {
                Failures.Add("Objeto ativo no ponto de desligamento.");
            }

            foreach (var key in thresholdCurve.keys)
            {
                var actual = Sample(clip, key.time, threshold);
                Debug.Log("ANIMATION_AUDIO_THRESHOLD value=" + key.value + " active=" + actual);
                if (actual != AnimationAudioModel.IsActiveValue(key.value))
                {
                    Failures.Add("Limiar de ativação diferente do Unity no valor " + key.value);
                }
            }
        }
        finally
        {
            AnimationMode.StopAnimationMode();
            UnityEngine.Object.DestroyImmediate(clip);
            UnityEngine.Object.DestroyImmediate(root);
            root = null;
        }
    }

    private static void MakeStepped(AnimationCurve curve)
    {
        for (var index = 0; index < curve.length; index++)
        {
            AnimationUtility.SetKeyLeftTangentMode(curve, index, AnimationUtility.TangentMode.Constant);
            AnimationUtility.SetKeyRightTangentMode(curve, index, AnimationUtility.TangentMode.Constant);
        }
    }

    private static bool Sample(AnimationClip clip, float time, GameObject target)
    {
        AnimationMode.BeginSampling();
        AnimationMode.SampleAnimationClip(root, clip, time);
        AnimationMode.EndSampling();
        return target.activeInHierarchy;
    }

    private static void CheckAudio()
    {
        if (EditorApplication.timeSinceStartup < audioCheckAt)
        {
            return;
        }

        EditorApplication.update -= CheckAudio;
        foreach (var voice in voices)
        {
            if (!voice.isPlaying || voice.timeSamples <= 0)
            {
                Failures.Add("AudioSource oculto não reproduziu fora do Play mode.");
            }
            UnityEngine.Object.DestroyImmediate(voice.gameObject);
        }
        voices = null;
        Finish();
    }

    private static void Finish()
    {
        UnityEngine.Object.DestroyImmediate(tone);
        tone = null;

        if (Failures.Count == 0)
        {
            Debug.Log("ANIMATION_AUDIO_VALIDATION_OK");
        }
        else
        {
            Debug.LogError("ANIMATION_AUDIO_VALIDATION_FAILED\n" + string.Join("\n", Failures));
        }

        if (Application.isBatchMode)
        {
            EditorApplication.Exit(Failures.Count == 0 ? 0 : 1);
        }
    }
}
