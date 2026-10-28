using Microsoft.Playwright;

// Usage: Runner <baseUrl> [browser=chromium|firefox|webkit]
var baseUrl = args.Length > 0 ? args[0].TrimEnd('/') : "http://localhost:5181";
var browserName = args.Length > 1 ? args[1] : "chromium";

// Counts the QuickGrid listeners that are really on document.body, without touching QuickGrid's code.
const string InitScript = """
    (() => {
      const live = [];
      const isQuickGrid = fn => typeof fn === 'function' && fn.toString().includes('columnOptionsElement');
      const add = EventTarget.prototype.addEventListener;
      const remove = EventTarget.prototype.removeEventListener;
      EventTarget.prototype.addEventListener = function (type, fn, options) {
        if (this === document.body && isQuickGrid(fn)) live.push({ type, fn });
        return add.call(this, type, fn, options);
      };
      EventTarget.prototype.removeEventListener = function (type, fn, options) {
        if (this === document.body) {
          const i = live.findIndex(l => l.type === type && l.fn === fn);
          if (i >= 0) live.splice(i, 1);
        }
        return remove.call(this, type, fn, options);
      };
      window.__qg = {
        inits: 0, stops: 0, hold: false, pending: [],
        liveBodyListeners: () => live.length,
        release() { const p = this.pending; this.pending = []; p.forEach(r => r()); },
      };
    })();
    """;

// Appended to QuickGrid.razor.js. The original init runs unchanged; "hold" only delays its reply, like network latency.
const string InitWrapper = """

    export function init(tableElement) {
      const result = originalInit(tableElement);
      window.__qg.inits++;
      const stop = result.stop;
      result.stop = () => { window.__qg.stops++; stop(); };
      if (!window.__qg.hold) return result;
      return new Promise(resolve => window.__qg.pending.push(() => resolve(result)));
    }
    """;

using var playwright = await Playwright.CreateAsync();
await using var browser = await playwright[browserName].LaunchAsync();

await RunAsync("1. control: leave after init has replied", async page =>
{
    await page.ClickAsync("#go");
    await WaitTitleAsync(page, "Grid page");
    await page.WaitForFunctionAsync("window.__qg.inits === 1");
    await page.WaitForTimeoutAsync(500);
    await page.ClickAsync("#leave");
    await WaitTitleAsync(page, "Other page");
    await page.WaitForTimeoutAsync(500);
});

await RunAsync("2. init race: leave while the init reply is in flight", async page =>
{
    await page.EvaluateAsync("window.__qg.hold = true");
    await page.ClickAsync("#go");
    await WaitTitleAsync(page, "Grid page");
    await page.WaitForFunctionAsync("window.__qg.inits === 1");
    await page.ClickAsync("#leave");
    await WaitTitleAsync(page, "Other page");
    await page.WaitForTimeoutAsync(500);
    await page.EvaluateAsync("window.__qg.release()");
    await page.WaitForTimeoutAsync(1000);
});

await RunAsync("3. init race repeated 3 times in one circuit", async page =>
{
    await page.EvaluateAsync("window.__qg.hold = true");
    await page.ClickAsync("#go");
    for (var i = 1; i <= 3; i++)
    {
        if (i > 1)
        {
            await page.ClickAsync("#back");
        }

        await WaitTitleAsync(page, "Grid page");
        await page.WaitForFunctionAsync($"window.__qg.inits === {i}");
        await page.ClickAsync("#leave");
        await WaitTitleAsync(page, "Other page");
        await page.WaitForTimeoutAsync(500);
        await page.EvaluateAsync("window.__qg.release()");
        await page.WaitForTimeoutAsync(1000);
    }
});

await RunAsync("4. import race (fixed by #62840): leave while the module is loading", async page =>
{
    await page.ClickAsync("#go");
    await WaitTitleAsync(page, "Grid page");
    await page.WaitForTimeoutAsync(300);
    await page.ClickAsync("#leave");
    await WaitTitleAsync(page, "Other page");
    await page.WaitForTimeoutAsync(500);
}, holdModuleUntilLeft: true);

async Task RunAsync(string name, Func<IPage, Task> steps, bool holdModuleUntilLeft = false)
{
    Console.WriteLine($"===== {browserName} / {name}");
    var context = await browser.NewContextAsync();
    var page = await context.NewPageAsync();
    page.Console += (_, message) => { if (message.Type is "error" or "warning") Console.WriteLine($"[console {message.Type}] {message.Text}"); };
    page.PageError += (_, error) => Console.WriteLine($"[page error] {error}");
    await page.AddInitScriptAsync(InitScript);

    var moduleGate = new TaskCompletionSource();
    await page.RouteAsync("**/QuickGrid.razor.js", async route =>
    {
        if (holdModuleUntilLeft)
        {
            await moduleGate.Task;
        }

        var response = await route.FetchAsync();
        var body = (await response.TextAsync()).Replace("export function init(tableElement) {", "function originalInit(tableElement) {");
        await route.FulfillAsync(new() { Response = response, Body = body + InitWrapper });
    });

    await page.GotoAsync(baseUrl);
    await page.Locator("#mode").Filter(new() { HasText = "interactive" }).WaitForAsync();

    var stepsTask = steps(page);
    if (holdModuleUntilLeft)
    {
        await stepsTask;
        moduleGate.SetResult();
        await page.WaitForTimeoutAsync(1000);
    }
    else
    {
        await stepsTask;
    }

    var inits = await page.EvaluateAsync<int>("window.__qg.inits");
    var stops = await page.EvaluateAsync<int>("window.__qg.stops");
    var live = await page.EvaluateAsync<int>("window.__qg.liveBodyListeners()");
    var tables = await page.EvaluateAsync<int>("document.querySelectorAll('table').length");
    Console.WriteLine($"page={await page.TextContentAsync("#title")}, tables in DOM={tables}, init called={inits}, stop called={stops}, QuickGrid listeners still on body={live}");
    Console.WriteLine();
    await context.CloseAsync();
}

static Task WaitTitleAsync(IPage page, string title)
    => page.Locator("#title").Filter(new() { HasText = title }).WaitForAsync();
