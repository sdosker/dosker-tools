using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DoskerTools.EditorUi;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using static DoskerTools.EditorUi.DoskerLocale;

// VRC Raycast no Edit mode. O VRCRaycastHandler do SDK só é iniciado no Play mode
// (RuntimeInitializeOnLoadMethod) e o Start do componente não roda no Edit mode, então
// a janela inicia o handler, registra os componentes e chama o mesmo par Submit/Complete
// que o VRCDynamicsScheduler chamaria. Os raios, as máscaras de layer e o resultado na
// Result Transform são os do próprio SDK.
public sealed partial class VrcEditModePreviewWindow
{
    private const string RaycastTypeName = "VRC.SDK3.Avatars.Components.VRCRaycast";
    private const string RaycastHandlerTypeName = "VRC.SDK3.Avatars.Components.VRCRaycastHandler";
    private const float RaycastFallbackDistance = 1000f;

    private static Type raycastType;
    private readonly List<Component> raycasts = new List<Component>();
    private readonly HashSet<Component> simulatedRaycasts = new HashSet<Component>();
    private readonly Dictionary<Component, RaycastState> raycastStates = new Dictionary<Component, RaycastState>();
    private RaycastRunner raycastRunner;
    private string raycastFailure;
    private bool showRaycasts = true;

    private sealed class RaycastState
    {
        public bool Valid;
        public bool Hit;
        public Vector3 Origin;
        public Vector3 Direction;
        public float MaxDistance;
        public Vector3 Point;
        public Vector3 Normal;
        public float Distance;
        public float Ratio;
        public string ColliderName;
    }

    // O SDK não prevê este uso: qualquer membro ausente ou erro desliga só os raycasts.
    private sealed class RaycastRunner
    {
        private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        // Os acessores de parâmetro escreveriam no Animator do avatar; sem eles o SDK só guarda os valores.
        private static readonly string[] ParameterAccessors = { "param_Hit", "param_Ratio", "param_Distance" };

        private readonly Type handlerType = FindType(RaycastHandlerTypeName);
        private readonly List<Component> active = new List<Component>();
        private readonly List<SavedBone> savedResults = new List<SavedBone>();
        private FieldInfo isInitialized;
        private FieldInfo resultBuffer;
        private FieldInfo submittedDirection;
        private FieldInfo cachedTransform;
        private MethodInfo initialize;
        private MethodInfo uninitialize;
        private MethodInfo removeRaycaster;
        private MethodInfo submit;
        private MethodInfo complete;
        private MethodInfo effectiveDistance;
        private PropertyInfo commandIndex;
        private PropertyInfo secondaryCommandIndex;
        private bool ownsHandler;

        public static RaycastRunner TryStart(List<Component> components, out string failure)
        {
            failure = null;
            var runner = new RaycastRunner();
            try
            {
                runner.Start(components);
                return runner;
            }
            catch (Exception exception)
            {
                failure = (exception.InnerException ?? exception).Message;
                runner.Stop();
                return null;
            }
        }

        private static MethodInfo Required(Type type, string name, int parameters)
        {
            var method = type?.GetMethods(Members).FirstOrDefault(item => item.Name == name && item.GetParameters().Length == parameters);
            if (method == null)
            {
                throw new MissingMethodException(type?.Name ?? "VRCRaycast", name);
            }
            return method;
        }

        private static FieldInfo RequiredField(Type type, string name)
        {
            var field = type?.GetField(name, Members);
            if (field == null)
            {
                throw new MissingFieldException(type?.Name ?? "VRCRaycast", name);
            }
            return field;
        }

        private static PropertyInfo RequiredProperty(Type type, string name)
        {
            var property = type?.GetProperty(name, Members);
            if (property == null)
            {
                throw new MissingMemberException(type?.Name ?? "VRCRaycast", name);
            }
            return property;
        }

        private void Start(List<Component> components)
        {
            isInitialized = RequiredField(handlerType, "_isInitialized");
            resultBuffer = RequiredField(handlerType, "_resultBuffer");
            initialize = Required(handlerType, "Initialize", 0);
            uninitialize = Required(handlerType, "Uninitialize", 0);
            removeRaycaster = Required(handlerType, "RemoveRaycaster", 1);
            submit = Required(handlerType, "SubmitRaycasts", 0);
            complete = Required(handlerType, "CompleteRaycasts", 0);
            var addRaycaster = Required(handlerType, "AddRaycaster", 1);
            var awake = Required(raycastType, "Awake", 0);
            submittedDirection = RequiredField(raycastType, "_submittedWorldSpaceDirection");
            cachedTransform = RequiredField(raycastType, "_cachedEffectiveTransform");
            effectiveDistance = Required(raycastType, "CalculateEffectiveDistance", 1);
            commandIndex = RequiredProperty(raycastType, "CommandIndex");
            secondaryCommandIndex = RequiredProperty(raycastType, "SecondaryCommandIndex");
            var accessors = ParameterAccessors.Select(name => RequiredField(raycastType, name)).ToArray();

            if (!(bool)isInitialized.GetValue(null))
            {
                initialize.Invoke(null, null);
                ownsHandler = true;
            }

            foreach (var raycast in components)
            {
                var result = Field<Transform>(raycast, "resultTransform");
                if (result != null && savedResults.All(saved => saved.Transform != result))
                {
                    savedResults.Add(new SavedBone { Transform = result, LocalPosition = result.localPosition, LocalRotation = result.localRotation });
                }
                foreach (var accessor in accessors)
                {
                    accessor.SetValue(raycast, null);
                }
                // Sem [ExecuteAlways] o Awake não roda: é ele quem monta as máscaras de layer.
                awake.Invoke(raycast, null);
                addRaycaster.Invoke(null, new object[] { raycast });
                active.Add(raycast);
            }
        }

        public void Tick(Dictionary<Component, RaycastState> states)
        {
            // No Edit mode a física não sincroniza as transforms sozinha antes de uma consulta.
            Physics.SyncTransforms();
            submit.Invoke(null, null);
            complete.Invoke(null, null);

            var results = (NativeArray<RaycastHit>)resultBuffer.GetValue(null);
            foreach (var raycast in active.Where(raycast => raycast != null))
            {
                if (!states.TryGetValue(raycast, out var state))
                {
                    state = new RaycastState();
                    states[raycast] = state;
                }
                var origin = cachedTransform.GetValue(raycast) as Transform;
                var primary = ReadHit(results, (int)commandIndex.GetValue(raycast));
                var secondary = ReadHit(results, (int)secondaryCommandIndex.GetValue(raycast));
                // A mesma escolha do HandleRaycastResult: o acerto mais perto entre os dois raios.
                var hit = primary.collider == null ? secondary
                    : secondary.collider == null ? primary
                    : primary.distance <= secondary.distance ? primary : secondary;
                state.Valid = origin != null;
                state.Origin = origin != null ? origin.position : raycast.transform.position;
                state.Direction = (Vector3)submittedDirection.GetValue(raycast);
                state.MaxDistance = (float)effectiveDistance.Invoke(raycast, new object[] { true });
                state.Hit = hit.collider != null;
                state.Point = hit.point;
                state.Normal = hit.normal;
                state.ColliderName = state.Hit ? hit.collider.name : null;
                if (string.IsNullOrEmpty(Field<string>(raycast, "parameter")))
                {
                    state.Distance = state.Hit ? hit.distance : 0f;
                    state.Ratio = state.Hit && state.MaxDistance > 0f ? Mathf.InverseLerp(0f, state.MaxDistance, hit.distance) : 0f;
                }
                else
                {
                    // Com parâmetro, o SDK já aplicou o Behavior On Miss: o valor é o que o animator receberia.
                    state.Distance = Field(raycast, "param_DistanceValue", 0f);
                    state.Ratio = Field(raycast, "param_RatioValue", 0f);
                }
            }
        }

        private static RaycastHit ReadHit(NativeArray<RaycastHit> results, int index)
        {
            return results.IsCreated && index >= 0 && index < results.Length ? results[index] : default;
        }

        public void RestoreResults()
        {
            foreach (var saved in savedResults.Where(saved => saved.Transform != null))
            {
                saved.Transform.localPosition = saved.LocalPosition;
                saved.Transform.localRotation = saved.LocalRotation;
            }
        }

        public void Stop()
        {
            try
            {
                if (removeRaycaster != null)
                {
                    foreach (var raycast in active.Where(raycast => raycast != null))
                    {
                        removeRaycaster.Invoke(null, new object[] { raycast });
                    }
                }
                if (ownsHandler)
                {
                    // Libera os NativeArrays e solta o evento do VRCDynamicsScheduler.
                    uninitialize.Invoke(null, null);
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning(Tr("VRC preview: erro ao parar os raycasts: ", "VRC preview: error stopping the raycasts: ") + (exception.InnerException ?? exception).Message);
            }
            finally
            {
                active.Clear();
                ownsHandler = false;
                RestoreResults();
            }
        }
    }

    private void StartRaycasts()
    {
        simulatedRaycasts.Clear();
        simulatedRaycasts.UnionWith(raycasts);
        raycastStates.Clear();
        if (raycastFailure != null || raycastType == null || raycasts.Count == 0)
        {
            return;
        }
        raycastRunner = RaycastRunner.TryStart(raycasts.Where(raycast => raycast != null).ToList(), out raycastFailure);
    }

    private void StopRaycasts()
    {
        raycastRunner?.Stop();
        raycastRunner = null;
        raycastStates.Clear();
    }

    // Roda onde o VRCDynamicsScheduler rodaria no cliente: depois do animator e antes dos PhysBones.
    private void TickRaycasts()
    {
        if (raycastRunner == null)
        {
            return;
        }
        try
        {
            raycastRunner.Tick(raycastStates);
        }
        catch (Exception exception)
        {
            raycastFailure = (exception.InnerException ?? exception).Message;
            StopRaycasts();
        }
    }

    private void DrawRaycastsCard()
    {
        if (raycastType == null)
        {
            return;
        }
        using (var card = new DoskerEditorUi.CardScope(
            "Vrc.Raycasts",
            "VRC Raycast",
            "d_Linked",
            DoskerEditorUi.Green,
            raycasts.Count.ToString(),
            ref showRaycasts))
        {
            if (!card.Expanded)
            {
                return;
            }
            if (raycasts.Count == 0)
            {
                EditorGUILayout.LabelField(Tr("Nenhum VRC Raycast nesta cena.", "No VRC Raycast in this scene."), EditorStyles.miniLabel);
                return;
            }
            if (!simulatePhysBones)
            {
                EditorGUILayout.LabelField(Tr("Os raios rodam junto com a simulação.", "The rays run with the simulation."), EditorStyles.miniLabel);
            }
            foreach (var raycast in raycasts.Where(raycast => raycast != null && MatchesSearch(raycast.name)))
            {
                DrawRaycastRow(raycast);
            }
        }
    }

    private void DrawRaycastRow(Component raycast)
    {
        var parameter = Field<string>(raycast, "parameter");
        raycastStates.TryGetValue(raycast, out var state);
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button(raycast.name, EditorStyles.label, GUILayout.Width(140f)))
            {
                Selection.activeObject = raycast.gameObject;
                EditorGUIUtility.PingObject(raycast.gameObject);
            }
            var rect = GUILayoutUtility.GetRect(60f, EditorGUIUtility.singleLineHeight, GUILayout.ExpandWidth(true));
            var name = string.IsNullOrEmpty(parameter) ? Tr("(sem parâmetro)", "(no parameter)") : parameter;
            if (state == null || !state.Valid)
            {
                DoskerEditorUi.ValueBar(rect, 0f, name, DoskerEditorUi.Neutral);
                return;
            }
            var label = string.Format(
                "{0}_Hit = {1} · _Ratio = {2:0.00} · _Distance = {3:0.00}{4}",
                name,
                state.Hit ? "true" : "false",
                state.Ratio,
                state.Distance,
                state.Hit ? " · " + state.ColliderName : string.Empty);
            DoskerEditorUi.ValueBar(rect, state.Hit ? state.Ratio : 0f, label, state.Hit ? DoskerEditorUi.Green : DoskerEditorUi.Neutral);
        }
    }

    private void DrawRaycasts()
    {
        foreach (var raycast in raycasts.Where(raycast => raycast != null))
        {
            raycastStates.TryGetValue(raycast, out var state);
            if (state == null || !state.Valid)
            {
                // Sem simulação: só o alcance, calculado como o SDK calcula.
                var origin = RootOf(raycast);
                var local = Field(raycast, "raycastDirection", Vector3.forward);
                var direction = origin.TransformDirection(local.sqrMagnitude > 0f ? local.normalized : Vector3.forward);
                Handles.color = WithOpacity(new Color(0.55f, 0.85f, 0.55f), 0.5f);
                Handles.DrawDottedLine(origin.position, origin.position + direction * StaticRaycastDistance(raycast, origin), 4f);
                continue;
            }
            var end = state.Hit ? state.Point : state.Origin + state.Direction * state.MaxDistance;
            Handles.color = WithOpacity(state.Hit ? new Color(0.3f, 1f, 0.4f) : new Color(0.6f, 0.6f, 0.6f));
            Handles.DrawLine(state.Origin, end);
            if (state.Hit)
            {
                var size = HandleUtility.GetHandleSize(state.Point) * 0.06f;
                Handles.DrawWireDisc(state.Point, state.Normal, size);
                Handles.DrawLine(state.Point, state.Point + state.Normal * size * 2f);
            }
            if (showLabels)
            {
                var parameter = Field<string>(raycast, "parameter");
                Handles.Label(end, string.Format("{0} {1:0.00} m", string.IsNullOrEmpty(parameter) ? raycast.name : parameter, state.Hit ? state.Distance : state.MaxDistance), EditorStyles.miniBoldLabel);
            }
        }
    }

    // Igual ao CalculateEffectiveDistance do SDK, para mostrar o alcance sem iniciar o handler.
    private static float StaticRaycastDistance(Component raycast, Transform origin)
    {
        var distance = Field(raycast, "distance", 0f);
        if (distance <= 0f || float.IsInfinity(distance) || float.IsNaN(distance))
        {
            return RaycastFallbackDistance;
        }
        if (Field(raycast, "applyTransformScale", false))
        {
            distance *= origin.lossyScale.z;
        }
        return Mathf.Clamp(distance, 0f, RaycastFallbackDistance);
    }
}
