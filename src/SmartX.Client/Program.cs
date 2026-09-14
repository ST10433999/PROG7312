using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using SmartX.Client;
using SmartX.Client.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// The API base URL lives in wwwroot/appsettings.json so the same build works locally and in Docker.
var apiBase = builder.Configuration["ApiBaseUrl"] ?? "http://localhost:5200/";
if (!apiBase.EndsWith('/')) apiBase += "/";

builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(apiBase) });
builder.Services.AddScoped<GatewayApiClient>();
builder.Services.AddSingleton<ToastService>();

await builder.Build().RunAsync();
