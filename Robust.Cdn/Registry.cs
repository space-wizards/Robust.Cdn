using Quartz;
using Robust.Cdn.Config;
using Robust.Cdn.Controllers;
using Robust.Cdn.Helpers;
using Robust.Cdn.Jobs;
using Robust.Cdn.Services;
using System.Net.Http.Headers;

namespace Robust.Cdn;

public static class Registry
{
    public static void SetupDependencies(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<CdnOptions>(configuration.GetSection(CdnOptions.Position));
        services.Configure<ManifestOptions>(configuration.GetSection(ManifestOptions.Position));

        services.AddControllersWithViews();
        services.AddScoped<BuildDirectoryManager>();
        services.AddSingleton<DownloadRequestLogger>();
        services.AddHostedService(s => s.GetRequiredService<DownloadRequestLogger>());
        services.AddScoped<Database>();
        services.AddScoped<ManifestDatabase>();
        services.AddScoped<PublishManager>();
        services.AddSingleton(TimeProvider.System);
        services.AddQuartz(q =>
        {
            q.AddJob<IngestNewCdnContentJob>(j => j.WithIdentity(IngestNewCdnContentJob.Key).StoreDurably());
            q.AddJob<MakeNewManifestVersionsAvailableJob>(j =>
            {
                j.WithIdentity(MakeNewManifestVersionsAvailableJob.Key).StoreDurably();
            });
            q.AddJob<NotifyWatchdogUpdateJob>(j => j.WithIdentity(NotifyWatchdogUpdateJob.Key).StoreDurably());
            q.AddJob<UpdateForkManifestJob>(j => j.WithIdentity(UpdateForkManifestJob.Key).StoreDurably());
            q.ScheduleJob<PruneOldManifestBuilds>(trigger => trigger.WithSimpleSchedule(schedule =>
            {
                schedule.RepeatForever().WithIntervalInHours(24);
            }));
            q.ScheduleJob<DeleteInProgressPublishesJob>(t =>
                t.WithSimpleSchedule(s => s.RepeatForever().WithIntervalInHours(24)));
        });

        services.AddQuartzHostedService(q =>
        {
            q.WaitForJobsToComplete = true;
        });

        const string userAgent = "Robust.Cdn";

        services.AddHttpClient(ForkPublishController.PublishFetchHttpClient, c =>
        {
            c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(userAgent, null));
        });
        services.AddHttpClient(NotifyWatchdogUpdateJob.HttpClientName, c =>
        {
            c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(userAgent, null));
        });

        services.AddScoped<BaseUrlManager>();
        services.AddScoped<ForkAuthHelper>();
        services.AddHttpContextAccessor();

        /*
        // Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();
        */
    }
}
