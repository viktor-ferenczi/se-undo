using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Gui;
using Sandbox.Game.Screens.Terminal.Controls;
using VRage.Utils;
using VRageMath;

namespace ClientPlugin.Ops;

// Reads and writes terminal control values as text. The controls are closed generic
// types (MyTerminalControlSlider<MyThrust>, ...), so GetValue and SetValue are
// reached through reflection, cached per control type.
public static class TerminalValues
{
    public sealed class Accessor
    {
        public Type ValueType;
        public MethodInfo Get;
        public MethodInfo Set;
    }

    private static readonly Dictionary<Type, Accessor> Accessors = new Dictionary<Type, Accessor>();
    private static readonly HashSet<Type> Unknown = new HashSet<Type>();

    // Null when the control holds no value or a value of a type the plugin cannot store
    public static Accessor AccessorOf(ITerminalControl control)
    {
        var type = control.GetType();
        lock (Accessors)
        {
            if (!Accessors.TryGetValue(type, out var accessor))
                Accessors[type] = accessor = Create(type);
            return accessor;
        }
    }

    private static Accessor Create(Type controlType)
    {
        var valueControl = controlType;
        while (
            valueControl != null
            && !(
                valueControl.IsGenericType
                && valueControl.GetGenericTypeDefinition() == typeof(MyTerminalValueControl<,>)
            )
        )
            valueControl = valueControl.BaseType;
        if (valueControl == null)
            return null;

        var arguments = valueControl.GetGenericArguments();
        var valueType = arguments[1];
        if (!IsSupported(valueType))
        {
            if (Unknown.Add(valueType))
                Log.Warning(
                    $"Terminal values of type {valueType.FullName} are not recorded, first seen on {controlType}"
                );
            return null;
        }

        return new Accessor
        {
            ValueType = valueType,
            Get = controlType.GetMethod("GetValue", new[] { arguments[0] }),
            Set = controlType.GetMethod("SetValue", arguments),
        };
    }

    private static bool IsSupported(Type type) =>
        type == typeof(bool)
        || type == typeof(float)
        || type == typeof(long)
        || type == typeof(Color)
        || type == typeof(StringBuilder)
        || type == typeof(MyStringId)
        || type.IsEnum;

    // The first value control with this id; the block's own controls come after
    // those of its base classes
    public static ITerminalControl Find(MyTerminalBlock block, string controlId)
    {
        foreach (var control in MyTerminalControlFactory.GetControls(block.GetType()))
        {
            if (control.Id == controlId && AccessorOf(control) != null)
                return control;
        }
        return null;
    }

    public static string Read(ITerminalControl control, MyTerminalBlock block)
    {
        var accessor = AccessorOf(control);
        return Format(accessor.Get.Invoke(control, new object[] { block }));
    }

    public static void Write(ITerminalControl control, MyTerminalBlock block, string text)
    {
        var accessor = AccessorOf(control);
        accessor.Set.Invoke(control, new[] { block, Parse(text, accessor.ValueType) });
    }

    private static string Format(object value) =>
        value switch
        {
            null => "",
            bool b => b ? "true" : "false",
            float f => f.ToString("R", CultureInfo.InvariantCulture),
            long l => l.ToString(CultureInfo.InvariantCulture),
            Color c => c.PackedValue.ToString(CultureInfo.InvariantCulture),
            MyStringId id => id.String,
            _ => value.ToString(), // StringBuilder and enums
        };

    private static object Parse(string text, Type type)
    {
        if (type == typeof(bool))
            return text == "true";
        if (type == typeof(float))
            return float.Parse(text, CultureInfo.InvariantCulture);
        if (type == typeof(long))
            return long.Parse(text, CultureInfo.InvariantCulture);
        if (type == typeof(Color))
            return new Color(uint.Parse(text, CultureInfo.InvariantCulture));
        if (type == typeof(StringBuilder))
            return new StringBuilder(text);
        if (type == typeof(MyStringId))
            return MyStringId.GetOrCompute(text);
        return Enum.Parse(type, text);
    }
}
