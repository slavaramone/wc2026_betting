using Microsoft.Playwright;

namespace Wc26.Betting.Core.Sofascore;

public sealed class SofascoreClient : IAsyncDisposable
{
    private const string BaseUrl = "https://www.sofascore.com";

    private readonly IPlaywright _playwright;
    private readonly IBrowser _browser;
    private readonly IBrowserContext _context;
    private readonly IPage _page;

    private SofascoreClient(IPlaywright playwright, IBrowser browser, IBrowserContext context, IPage page)
    {
        _playwright = playwright;
        _browser = browser;
        _context = context;
        _page = page;
    }

    public static async Task<SofascoreClient> CreateAsync(
        SofascoreGrabRequest request,
        TextWriter log,
        CancellationToken cancellationToken)
    {
        await log.WriteLineAsync("Starting Playwright Chromium session for SofaScore...");

        var playwright = await Playwright.CreateAsync();
        IBrowser? browser = null;
        IBrowserContext? context = null;
        IPage? page = null;

        try
        {
            browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = request.Headless
            });

            // Keep this close to the working LiveTotalsHelper pattern.
            // SofaScore is more reliable when the API requests are made from a warmed browser context.
            context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                UserAgent = request.UserAgent
            });

            page = await context.NewPageAsync();

            await log.WriteLineAsync($"Opening warmup page: {request.WarmupUrl}");
            await page.GotoAsync(request.WarmupUrl);

            if (request.WarmupDelayMs > 0)
                await Task.Delay(request.WarmupDelayMs, cancellationToken);

            await log.WriteLineAsync("SofaScore browser context is ready.");
            return new SofascoreClient(playwright, browser, context, page);
        }
        catch
        {
            if (page is not null)
                await page.CloseAsync();
            if (context is not null)
                await context.CloseAsync();
            if (browser is not null)
                await browser.CloseAsync();
            playwright.Dispose();
            throw;
        }
    }

    public Task<string> GetCalendarAsync(SofascoreGrabRequest request, SofascoreRoundRequest round, CancellationToken cancellationToken)
    {
        var url = $"{BaseUrl}/api/v1/unique-tournament/{request.TournamentId}/season/{request.SeasonId}/events/{round.CalendarPathSegment}";
        return GetStringWithRetryAsync(url, cancellationToken);
    }

    public Task<string> GetIncidentsAsync(long eventId, CancellationToken cancellationToken)
    {
        var url = $"{BaseUrl}/api/v1/event/{eventId}/incidents";
        return GetStringWithRetryAsync(url, cancellationToken);
    }

    public Task<string> GetStatisticsAsync(long eventId, CancellationToken cancellationToken)
    {
        var url = $"{BaseUrl}/api/v1/event/{eventId}/statistics";
        return GetStringWithRetryAsync(url, cancellationToken);
    }

    private async Task<string> GetStringWithRetryAsync(string url, CancellationToken cancellationToken)
    {
        const int maxAttempts = 3;
        Exception? lastException = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var response = await _page.APIRequest.GetAsync(url);
                var content = await response.TextAsync();

                if (response.Ok)
                    return content;

                // Some SofaScore endpoints are stricter for APIRequest than for real in-page fetch.
                // Fall back to fetch() executed inside the warmed-up SofaScore page context.
                if (response.Status == 403)
                    return await FetchFromPageContextAsync(url);

                throw new HttpRequestException($"GET {url} failed with {response.Status} {response.StatusText}. Body: {Truncate(content, 500)}");
            }
            catch (Exception ex) when (attempt < maxAttempts && ex is not OperationCanceledException)
            {
                lastException = ex;
                await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt), cancellationToken);
            }
        }

        throw lastException ?? new HttpRequestException($"GET {url} failed.");
    }

    private async Task<string> FetchFromPageContextAsync(string url)
    {
        const string script = @"
            async (url) => {
                const response = await fetch(url, {
                    method: 'GET',
                    credentials: 'include',
                    headers: {
                        'accept': 'application/json, text/plain, */*'
                    }
                });
                const text = await response.text();
                if (!response.ok) {
                    throw new Error(`GET ${url} failed with ${response.status} ${response.statusText}. Body: ${text.substring(0, 500)}`);
                }
                return text;
            }";

        return await _page.EvaluateAsync<string>(script, url);
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength] + "...";

    public async ValueTask DisposeAsync()
    {
        await _page.CloseAsync();
        await _context.CloseAsync();
        await _browser.CloseAsync();
        _playwright.Dispose();
    }
}
