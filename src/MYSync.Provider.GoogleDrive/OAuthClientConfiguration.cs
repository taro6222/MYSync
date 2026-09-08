using System.Text.Json;

namespace MYSync.Provider.GoogleDrive;

public static class OAuthClientConfiguration
{
    public static IReadOnlyDictionary<string, string> LoadEmbedded()
    {
        using var stream = typeof(OAuthClientConfiguration).Assembly.GetManifestResourceStream("MYSync.GoogleOAuth");
        if (stream is null) throw new InvalidOperationException("배포용 Google 로그인 설정이 없습니다.");
        return Parse(new StreamReader(stream).ReadToEnd());
    }
    public static IReadOnlyDictionary<string, string> Load(string path)
    {
        if (!File.Exists(path)) throw new InvalidOperationException("이 빌드는 Google 로그인이 아직 준비되지 않았습니다. 앱 배포자가 Google 연결 설정을 완료해야 합니다.");
        return Parse(File.ReadAllText(path));
    }
    private static IReadOnlyDictionary<string, string> Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var installed = doc.RootElement.GetProperty("installed");
            var id = installed.GetProperty("client_id").GetString();
            var secret = installed.GetProperty("client_secret").GetString();
            if (string.IsNullOrWhiteSpace(id) || !id.EndsWith(".apps.googleusercontent.com", StringComparison.Ordinal)
                || id.Any(char.IsWhiteSpace) || string.IsNullOrWhiteSpace(secret)) throw new InvalidDataException();
            return new Dictionary<string, string> { ["client_id"] = id, ["client_secret"] = secret };
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or InvalidDataException)
        { throw new InvalidOperationException("앱의 Google 로그인 설정이 올바르지 않습니다. 배포자에게 문의하세요."); }
    }
}
