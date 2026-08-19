using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System.Net;
using System.Text;
using System.Text.Json;

namespace NEXTHOS.Prompts;

public sealed class MainForm : Form
{
    private readonly WebView2 web = new() { Dock = DockStyle.Fill };
    private readonly HttpClient client;

    public MainForm()
    {
        Text = "NEXTHOS PROMPTS";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1024, 680);
        Size = new Size(1440, 900);
        BackColor = Color.FromArgb(6, 7, 10);
        Controls.Add(web);

        var clientHandler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip |
                                     DecompressionMethods.Deflate |
                                     DecompressionMethods.Brotli,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10
        };

        client = new HttpClient(clientHandler)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        Shown += async (_, _) => await InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        var userData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NEXTHOS", "WebView2");

        var env = await CoreWebView2Environment.CreateAsync(null, userData);
        await web.EnsureCoreWebView2Async(env);

        var settings = web.CoreWebView2.Settings;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreDevToolsEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.IsStatusBarEnabled = false;
        web.ZoomFactor = 1.0;

        web.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
        web.CoreWebView2.NewWindowRequested += (_, e) => e.Handled = true;

        var webRoot = Path.Combine(AppContext.BaseDirectory, "Web");
        web.CoreWebView2.SetVirtualHostNameToFolderMapping(
            "app.nexthos.local",
            webRoot,
            CoreWebView2HostResourceAccessKind.Allow);

        web.Source = new Uri("https://app.nexthos.local/index.html");
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var type) || type.GetString() != "transcribeTikTok") return;

            var tiktokUrl = root.TryGetProperty("url", out var u) ? u.GetString()?.Trim() : null;

            if (string.IsNullOrWhiteSpace(tiktokUrl) ||
                !Uri.TryCreate(tiktokUrl, UriKind.Absolute, out var uri) ||
                !(uri.Host.Equals("tiktok.com", StringComparison.OrdinalIgnoreCase) ||
                  uri.Host.EndsWith(".tiktok.com", StringComparison.OrdinalIgnoreCase)))
            {
                await SendErrorAsync("Link do TikTok inválido.");
                return;
            }

            var result = await TranscribeAsync(tiktokUrl);

            web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new
            {
                type = "transcriptionResult",
                data = result
            }));
        }
        catch (Exception ex)
        {
            await SendErrorAsync(ex.Message);
        }
    }

    private async Task<JsonElement> TranscribeAsync(string tiktokUrl)
    {
        using var request = new HttpRequestMessage
        {
            Method = HttpMethod.Post,
            RequestUri = new Uri("https://submagic-free-tools.fly.dev/api/tiktok-transcription"),
            Content = new StringContent(JsonSerializer.Serialize(new { url = tiktokUrl }), Encoding.UTF8, "application/json")
        };

        request.Headers.Host = "submagic-free-tools.fly.dev";
        request.Headers.Connection.Add("keep-alive");
        request.Headers.Accept.ParseAdd("*/*");
        request.Headers.AcceptEncoding.ParseAdd("gzip");
        request.Headers.AcceptEncoding.ParseAdd("deflate");
        request.Headers.AcceptEncoding.ParseAdd("br");
        request.Headers.AcceptLanguage.ParseAdd("pt-BR,pt;q=0.9,en-US;q=0.8,en;q=0.7");
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/151.0.0.0 Safari/537.36");
        request.Headers.Referrer = new Uri("https://submagic-free-tools.fly.dev/tiktok-transcription");
        request.Headers.TryAddWithoutValidation("Origin", "https://submagic-free-tools.fly.dev");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Storage-Access", "active");
        request.Headers.TryAddWithoutValidation("sec-ch-ua-platform", "\"Windows\"");
        request.Headers.TryAddWithoutValidation("sec-ch-ua", "\"Not=A?Brand\";v=\"99\", \"Google Chrome\";v=\"151\", \"Chromium\";v=\"151\"");
        request.Headers.TryAddWithoutValidation("sec-ch-ua-mobile", "?0");

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Erro HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {body}");

        using var parsed = JsonDocument.Parse(body);
        if (!parsed.RootElement.TryGetProperty("transcripts", out _))
            throw new InvalidOperationException("A resposta não contém transcrição.");

        return parsed.RootElement.Clone();
    }

    private Task SendErrorAsync(string message)
    {
        if (web.CoreWebView2 == null) return Task.CompletedTask;
        web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "transcriptionError", message }));
        return Task.CompletedTask;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            client.Dispose();
            web.Dispose();
        }
        base.Dispose(disposing);
    }
}
