using System;
using System.Collections.Generic;
using Sandbox.Graphics.GUI;
using VRageMath;

namespace ClientPlugin.Settings.Elements;

// A line of explanation above the option it is attached to
internal class NoteAttribute : Attribute, IElement
{
    public readonly string Text;

    public NoteAttribute(string text)
    {
        Text = text;
    }

    public List<Control> GetControls(
        string name,
        Func<object> propertyGetter,
        Action<object> propertySetter
    )
    {
        var label = new MyGuiControlLabel(text: Text, textScale: 0.7f)
        {
            ColorMask = Color.LightGray,
        };
        return new List<Control> { new Control(label, fillFactor: 1f) };
    }

    public List<Type> SupportedTypes { get; } = new List<Type> { typeof(object) };
}
