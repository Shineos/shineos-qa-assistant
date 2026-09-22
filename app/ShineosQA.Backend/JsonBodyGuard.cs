using System.Text;

namespace ShineosQA.Backend;

/// <summary>リクエストJSONの文字コード不正の分類。
/// Git BashのcurlなどがUTF-8以外（例: CP932）で日本語を送ると JsonDocument.GetString が
/// DecoderFallbackException 由来の InvalidOperationException を投げる。v2.1.12までは
/// 説明のないHTTP 500になっていたため、ミドルウェアで400に変換して区別できるようにする</summary>
public static class JsonBodyGuard
{
    /// <summary>JSON本文の文字コード不正（UTF-8として解読できないバイト列）による失敗か</summary>
    public static bool IsEncodingError(Exception ex) =>
        ex is DecoderFallbackException || ex.InnerException is DecoderFallbackException;
}
