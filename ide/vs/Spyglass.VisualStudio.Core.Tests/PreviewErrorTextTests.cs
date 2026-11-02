using Spyglass.VisualStudio.Core.Preview;
using Xunit;

namespace Spyglass.VisualStudio.Core.Tests;

public class PreviewErrorTextTests
{
    [Fact]
    public void TargetInvocationException_IsSummarisedByItsInnerCause()
    {
        var tie = "System.Reflection.TargetInvocationException: Exception has been thrown by the target of an invocation.\r\n" +
                  " ---> System.ArgumentException: -5 is not a valid value for 'MinWidth'.\r\n   at Avalonia.Layout.Layoutable.Validate()\r\n" +
                  "--- End of stack trace from previous location ---\r\n   --- End of inner exception stack trace ---\r\n   at System.Reflection.Invoke()";
        Assert.Equal("System.ArgumentException: -5 is not a valid value for 'MinWidth'.", PreviewErrorText.Summary(tie));
    }

    [Theory]
    [InlineData("XamlX.XamlParseException: bad value. Line 1, position 9.\n ---> System.FormatException: bad\n   --- End of inner exception stack trace ---", "XamlX.XamlParseException: bad value. Line 1, position 9.")]
    [InlineData("System.Reflection.TargetInvocationException: x\n   at Foo()", "System.Reflection.TargetInvocationException: x")]
    [InlineData("\n System.Xml.XmlException: bad\n   at Foo()", "System.Xml.XmlException: bad")]
    [InlineData("", "")]
    public void KeepsRealOuterExceptions_AndDropsStackTraces(string error, string expected) => Assert.Equal(expected, PreviewErrorText.Summary(error));
}
