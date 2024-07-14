using System.CommandLine;
using Robust.Cdn.Lib;

var rootCommand = new RootCommand();

{
    var downloadDestinationArgument = new Argument<FileInfo>("destination");
    var downloadUrlArgument = new Argument<string>("url");
    var downloadIndexArgument = new Argument<int>("index");
    var downloadIndexFromUrlCommand = new Command("index-from-url");
    downloadIndexFromUrlCommand.AddArgument(downloadUrlArgument);
    downloadIndexFromUrlCommand.AddArgument(downloadIndexArgument);
    downloadIndexFromUrlCommand.AddArgument(downloadDestinationArgument);
    downloadIndexFromUrlCommand.SetHandler(async (url, index, destination) =>
    {
        using var httpClient = new HttpClient();
        using var downloader = await Downloader.DownloadFilesAsync(httpClient, url, [index]);

        using var file = destination.Create();

        await downloader.ReadFileHeaderAsync();
        await downloader.ReadFileContentsAsync(file);

    }, downloadUrlArgument, downloadIndexArgument, downloadDestinationArgument);
    rootCommand.AddCommand(downloadIndexFromUrlCommand);
}

await rootCommand.InvokeAsync(args);
