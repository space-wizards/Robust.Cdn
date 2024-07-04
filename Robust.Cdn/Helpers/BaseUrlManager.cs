namespace Robust.Cdn.Helpers;

public sealed class BaseUrlManager(IConfiguration configuration)
{
    public string MakeBuildInfoUrl(string path)
    {
        var baseUrl = configuration[ConfigurationKeys.KeyBaseUrl];

        return baseUrl + path;
    }

    public void ValidateBaseUrl()
    {
        var baseUrl = configuration[ConfigurationKeys.KeyBaseUrl];

        if (baseUrl is null)
            throw new InvalidOperationException("BaseUrl is not set!");

        if (!baseUrl.EndsWith('/'))
            throw new InvalidOperationException("BaseUrl must end with '/'!");
    }
}
