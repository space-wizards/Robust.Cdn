using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Options;
using Quartz;
using Robust.Cdn.Config;

namespace Robust.Cdn.Jobs;

/// <summary>
/// Job responsible for notifying <c>SS14.Watchdog</c> instances that a new update is available.
/// </summary>
/// <remarks>
/// This job is triggered by <see cref="MakeNewManifestVersionsAvailableJob"/>.
/// </remarks>
public sealed class NotifyWatchdogUpdateJob(
    IHttpClientFactory httpClientFactory,
    ILogger<NotifyWatchdogUpdateJob> logger,
    IOptions<ManifestOptions> manifestOptions) : IJob
{
    public static readonly JobKey Key = new(nameof(NotifyWatchdogUpdateJob));

    public const string KeyForkName = "ForkName";

    public const string HttpClientName = "NotifyWatchdogUpdateJob";

    public static JobDataMap Data(string fork) => new()
    {
        { KeyForkName, fork },
    };

    public async Task Execute(IJobExecutionContext context)
    {
        var fork = context.MergedJobDataMap.GetString(KeyForkName) ?? throw new InvalidDataException();
        var config = manifestOptions.Value.Forks[fork];

        if (config.NotifyWatchdogs.Length == 0)
            return;

        logger.LogInformation("Notifying watchdogs of update for fork {Fork}", fork);

        var httpClient = httpClientFactory.CreateClient(HttpClientName);

        await Task.WhenAll(
            config.NotifyWatchdogs.Select(notify => SendNotify(notify, httpClient, context.CancellationToken)));
    }

    private async Task SendNotify(
        ManifestForkNotifyWatchdog watchdog,
        HttpClient client,
        CancellationToken cancel)
    {
        logger.LogDebug(
            "Sending watchdog update notify to {WatchdogUrl} instance {Instance}",
            watchdog.WatchdogUrl,
            watchdog.Instance);

        var url = NormalizeTrailingSlash(watchdog.WatchdogUrl) + $"instances/{watchdog.Instance}/update";
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            FormatBasicAuth(watchdog.Instance, watchdog.ApiToken));

        try
        {
            using var response = await client.SendAsync(request, cancel);

            if (!response.IsSuccessStatusCode)
            {
                var responseContent = await response.Content.ReadAsStringAsync(cancel);
                logger.LogWarning(
                    "Update notify to {WatchdogUrl} instance {Instance} did not indicate success ({Status}): {ResponseContent}",
                    watchdog.WatchdogUrl, watchdog.Instance, response.StatusCode, responseContent);
            }
        }
        catch (Exception e)
        {
            logger.LogWarning(
                e,
                "Error while notifying watchdog {WatchdogUrl} instance {Instance} of update",
                watchdog.WatchdogUrl,
                watchdog.Instance);
        }
    }

    private static string NormalizeTrailingSlash(string url)
    {
        return url.EndsWith('/') ? url : url + '/';
    }

    private static string FormatBasicAuth(string user, string password)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));
    }
}
