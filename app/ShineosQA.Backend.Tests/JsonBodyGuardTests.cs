using System.Text;
using ShineosQA.Backend;
using Xunit;

namespace ShineosQA.Backend.Tests;

public class JsonBodyGuardTests
{
    [Fact]
    public void DecoderFallbackException_IsEncodingError()
    {
        var ex = new DecoderFallbackException("Unable to translate bytes");
        Assert.True(JsonBodyGuard.IsEncodingError(ex));
    }

    [Fact]
    public void TranscodeWrap_IsEncodingError()
    {
        // 実際の例外: InvalidOperationException（内側にDecoderFallbackException）
        var inner = new DecoderFallbackException("Unable to translate bytes [83]");
        var wrapped = new InvalidOperationException("Cannot transcode invalid UTF-8 JSON text to UTF-16 string.", inner);
        Assert.True(JsonBodyGuard.IsEncodingError(wrapped));
    }

    [Fact]
    public void OtherExceptions_AreNotEncodingErrors()
    {
        Assert.False(JsonBodyGuard.IsEncodingError(new InvalidOperationException("unrelated")));
        Assert.False(JsonBodyGuard.IsEncodingError(new KeyNotFoundException("unknown model id")));
        // 内側が文字コード系でなければfalse
        Assert.False(JsonBodyGuard.IsEncodingError(new InvalidOperationException("wrap", new InvalidDataException("sha mismatch"))));
    }
}
