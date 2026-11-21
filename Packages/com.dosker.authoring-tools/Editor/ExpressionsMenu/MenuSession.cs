// Estado simulado do menu: caminho dos submenus, valores dos parâmetros e o puppet aberto.
// As regras seguem a documentação do VRChat sobre os controles do menu de expressões.
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using ControlType = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control.ControlType;
using ValueType = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters.ValueType;

namespace DoskerTools.AuthoringTools
{
    internal sealed class MenuSession
    {
        // O botão volta a zero depois da sincronização, cerca de um segundo depois.
        internal const double ButtonHold = 1.0;
        internal const int MaxControls = 8;

        private readonly Dictionary<string, float> values = new Dictionary<string, float>();
        private readonly Dictionary<string, ValueType> types = new Dictionary<string, ValueType>();
        private readonly List<Level> path = new List<Level>();

        private VRCExpressionsMenu.Control button;
        private double buttonUntil;

        public MenuSession(VRCExpressionsMenu root, VRCExpressionParameters parameters, VRCAvatarDescriptor avatar)
        {
            Root = root;
            Parameters = parameters;
            Avatar = avatar;
            Reset();
        }

        public VRCExpressionsMenu Root { get; }

        public VRCExpressionParameters Parameters { get; private set; }

        public VRCAvatarDescriptor Avatar { get; private set; }

        public VRCExpressionsMenu.Control Puppet { get; private set; }

        public VRCExpressionsMenu Current => path.Count > 0 ? path[path.Count - 1].Menu : Root;

        public int Depth => path.Count;

        public IEnumerable<VRCExpressionsMenu> Path => path.Select(level => level.Menu);

        public IReadOnlyDictionary<string, float> Values => values;

        public bool HasPendingButton => button != null;

        public void Reset()
        {
            values.Clear();
            types.Clear();
            path.Clear();
            path.Add(new Level(Root, null));
            Puppet = null;
            button = null;

            if (Parameters != null && Parameters.parameters != null)
            {
                foreach (var parameter in Parameters.parameters)
                {
                    if (parameter == null || string.IsNullOrEmpty(parameter.name) || types.ContainsKey(parameter.name))
                    {
                        continue;
                    }
                    types[parameter.name] = parameter.valueType;
                    values[parameter.name] = Coerce(parameter.valueType, parameter.defaultValue);
                }
            }
        }

        public void UseParameters(VRCExpressionParameters parameters, VRCAvatarDescriptor avatar)
        {
            if (Parameters == parameters && Avatar == avatar)
            {
                return;
            }
            Parameters = parameters;
            Avatar = avatar;
            Reset();
        }

        public bool IsKnown(string parameter)
        {
            return !string.IsNullOrEmpty(parameter) && types.ContainsKey(parameter);
        }

        public bool TryGetType(string parameter, out ValueType type)
        {
            return types.TryGetValue(parameter ?? string.Empty, out type);
        }

        public float Get(string parameter)
        {
            return !string.IsNullOrEmpty(parameter) && values.TryGetValue(parameter, out var value) ? value : 0f;
        }

        public void Set(string parameter, float value)
        {
            if (string.IsNullOrEmpty(parameter))
            {
                return;
            }
            values[parameter] = types.TryGetValue(parameter, out var type) ? Coerce(type, value) : value;
            CloseMismatchedMenus(parameter);
        }

        // Ligado quando o parâmetro tem o valor do controle, como o VRChat marca toggles e botões.
        public bool IsActive(VRCExpressionsMenu.Control control)
        {
            if (control == null || control.parameter == null || string.IsNullOrEmpty(control.parameter.name))
            {
                return false;
            }
            var name = control.parameter.name;
            var expected = types.TryGetValue(name, out var type) ? Coerce(type, control.value) : control.value;
            return Mathf.Abs(Get(name) - expected) < 0.0001f;
        }

        public static IReadOnlyList<VRCExpressionsMenu.Control> Controls(VRCExpressionsMenu menu)
        {
            if (menu == null || menu.controls == null)
            {
                return new VRCExpressionsMenu.Control[0];
            }
            return menu.controls.Where(control => control != null).Take(MaxControls).ToList();
        }

        public void Activate(VRCExpressionsMenu.Control control, double now)
        {
            if (control == null)
            {
                return;
            }
            var parameter = control.parameter != null ? control.parameter.name : null;
            switch (control.type)
            {
                case ControlType.Button:
                    ReleaseButton();
                    Set(parameter, control.value);
                    button = control;
                    buttonUntil = now + ButtonHold;
                    break;
                case ControlType.Toggle:
                    Set(parameter, IsActive(control) ? 0f : control.value);
                    break;
                case ControlType.SubMenu:
                    if (control.subMenu == null)
                    {
                        break;
                    }
                    path.Add(new Level(control.subMenu, control));
                    Set(parameter, control.value);
                    break;
                case ControlType.TwoAxisPuppet:
                case ControlType.FourAxisPuppet:
                case ControlType.RadialPuppet:
                    Puppet = control;
                    Set(parameter, control.value);
                    if (control.type != ControlType.RadialPuppet)
                    {
                        SetAxes(Vector2.zero);
                    }
                    break;
            }
        }

        public void Back()
        {
            if (Puppet != null)
            {
                ClosePuppet();
                return;
            }
            if (path.Count <= 1)
            {
                return;
            }
            var level = path[path.Count - 1];
            path.RemoveAt(path.Count - 1);
            if (level.Entry != null && level.Entry.parameter != null)
            {
                Set(level.Entry.parameter.name, 0f);
            }
        }

        public void BackToRoot()
        {
            ClosePuppet();
            while (path.Count > 1)
            {
                Back();
            }
        }

        // O parâmetro do puppet só fica ligado enquanto ele está aberto; os eixos guardam o último valor.
        public void ClosePuppet()
        {
            if (Puppet == null)
            {
                return;
            }
            var control = Puppet;
            Puppet = null;
            if (control.parameter != null)
            {
                Set(control.parameter.name, 0f);
            }
        }

        public float RadialValue => Get(SubParameter(Puppet, 0));

        public void SetRadial(float value)
        {
            Set(SubParameter(Puppet, 0), Mathf.Clamp01(value));
        }

        // Dois eixos: horizontal e vertical de -1 a 1. Quatro eixos: cima, direita, baixo e esquerda de 0 a 1.
        public Vector2 Axes
        {
            get
            {
                if (Puppet == null)
                {
                    return Vector2.zero;
                }
                if (Puppet.type == ControlType.TwoAxisPuppet)
                {
                    return new Vector2(Get(SubParameter(Puppet, 0)), Get(SubParameter(Puppet, 1)));
                }
                return new Vector2(Get(SubParameter(Puppet, 1)) - Get(SubParameter(Puppet, 3)), Get(SubParameter(Puppet, 0)) - Get(SubParameter(Puppet, 2)));
            }
        }

        public void SetAxes(Vector2 axes)
        {
            if (Puppet == null)
            {
                return;
            }
            axes = Vector2.ClampMagnitude(axes, 1f);
            if (Puppet.type == ControlType.TwoAxisPuppet)
            {
                Set(SubParameter(Puppet, 0), axes.x);
                Set(SubParameter(Puppet, 1), axes.y);
                return;
            }
            Set(SubParameter(Puppet, 0), Mathf.Max(axes.y, 0f));
            Set(SubParameter(Puppet, 1), Mathf.Max(axes.x, 0f));
            Set(SubParameter(Puppet, 2), Mathf.Max(-axes.y, 0f));
            Set(SubParameter(Puppet, 3), Mathf.Max(-axes.x, 0f));
        }

        public static string SubParameter(VRCExpressionsMenu.Control control, int index)
        {
            if (control == null || control.subParameters == null || index >= control.subParameters.Length || control.subParameters[index] == null)
            {
                return null;
            }
            return control.subParameters[index].name;
        }

        // Devolve true enquanto um botão espera para voltar a zero.
        public bool Tick(double now)
        {
            if (button == null)
            {
                return false;
            }
            if (now >= buttonUntil)
            {
                ReleaseButton();
            }
            return true;
        }

        private void ReleaseButton()
        {
            if (button == null)
            {
                return;
            }
            var released = button;
            button = null;
            if (released.parameter != null)
            {
                Set(released.parameter.name, 0f);
            }
        }

        // "Changing the value of the parameter will close any sub-menus set to use a different value."
        private void CloseMismatchedMenus(string parameter)
        {
            for (var i = 1; i < path.Count; i++)
            {
                var entry = path[i].Entry;
                if (entry == null || entry.parameter == null || entry.parameter.name != parameter || IsActive(entry))
                {
                    continue;
                }
                path.RemoveRange(i, path.Count - i);
                Puppet = null;
                return;
            }
        }

        private static float Coerce(ValueType type, float value)
        {
            switch (type)
            {
                case ValueType.Bool:
                    return Mathf.Abs(value) > 0.0001f ? 1f : 0f;
                case ValueType.Int:
                    return Mathf.Clamp(Mathf.RoundToInt(value), 0, 255);
                default:
                    return Mathf.Clamp(value, -1f, 1f);
            }
        }

        // Procura nas cenas abertas um avatar cujo menu chega até este, para usar os parâmetros dele.
        public static VRCAvatarDescriptor FindAvatar(VRCExpressionsMenu menu)
        {
            if (menu == null)
            {
                return null;
            }
            foreach (var avatar in Object.FindObjectsOfType<VRCAvatarDescriptor>(true))
            {
                if (avatar != null && avatar.customExpressions && Reaches(avatar.expressionsMenu, menu, new HashSet<VRCExpressionsMenu>()))
                {
                    return avatar;
                }
            }
            return null;
        }

        private static bool Reaches(VRCExpressionsMenu from, VRCExpressionsMenu target, HashSet<VRCExpressionsMenu> visited)
        {
            if (from == null || !visited.Add(from))
            {
                return false;
            }
            if (from == target)
            {
                return true;
            }
            return from.controls != null && from.controls.Any(control => control != null && control.type == ControlType.SubMenu && Reaches(control.subMenu, target, visited));
        }

        public static string DisplayName(Object asset)
        {
            return asset != null ? asset.name : "?";
        }

        private readonly struct Level
        {
            public readonly VRCExpressionsMenu Menu;
            public readonly VRCExpressionsMenu.Control Entry;

            public Level(VRCExpressionsMenu menu, VRCExpressionsMenu.Control entry)
            {
                Menu = menu;
                Entry = entry;
            }
        }
    }
}
