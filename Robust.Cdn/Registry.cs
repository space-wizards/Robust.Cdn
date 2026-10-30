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
        // options
        services.Configure<CdnOptions>(configuration.GetSection(CdnOptions.Position));
        services.Configure<ManifestOptions>(configuration.GetSection(ManifestOptions.Position));

        // application services and database
        services.AddScoped<BuildDirectoryManager>();
        services.AddScoped<Database>();
        services.AddScoped<ManifestDatabase>();
        services.AddScoped<PublishManager>();

        // quartz
        ConfigureQuartz(services);

        // http-client-related
        const string userAgent = "Robust.Cdn";

        services.AddHttpClient(ForkPublishController.PublishFetchHttpClient, c =>
        {
            c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(userAgent, null));
        });
        services.AddHttpClient(NotifyWatchdogUpdateJob.HttpClientName, c =>
        {
            c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(userAgent, null));
        });

        // general system and pipeline
        services.AddSingleton(TimeProvider.System);
        services.AddControllersWithViews();
        services.AddScoped<BaseUrlManager>();
        services.AddScoped<ForkAuthHelper>();
        services.AddHttpContextAccessor();

        services.AddSingleton<DownloadRequestLogger>();
        services.AddHostedService(s => s.GetRequiredService<DownloadRequestLogger>());

        /*
        // Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();
        */
    }

    private static void ConfigureQuartz(IServiceCollection services)
    {
        services.AddQuartz(q =>
        {
            q.AddJob<IngestNewCdnContentJob>(
                j => j.WithIdentity(IngestNewCdnContentJob.Key).StoreDurably()
            );
            q.AddJob<MakeNewManifestVersionsAvailableJob>(
                j => j.WithIdentity(MakeNewManifestVersionsAvailableJob.Key).StoreDurably()
            );
            q.AddJob<NotifyWatchdogUpdateJob>(
                j => j.WithIdentity(NotifyWatchdogUpdateJob.Key).StoreDurably()
            );
            q.AddJob<UpdateForkManifestJob>(
                j => j.WithIdentity(UpdateForkManifestJob.Key).StoreDurably()
            );
            q.ScheduleJob<PruneOldManifestBuilds>(
                trigger => trigger.WithSimpleSchedule(schedule => schedule.RepeatForever().WithIntervalInHours(24))
            );
            q.ScheduleJob<DeleteInProgressPublishesJob>(
                t => t.WithSimpleSchedule(s => s.RepeatForever().WithIntervalInHours(24))
            );
        });

        services.AddQuartzHostedService(q =>
        {
            q.WaitForJobsToComplete = true;
        });
    }
}
