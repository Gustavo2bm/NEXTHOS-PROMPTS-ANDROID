using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System.Net;
using System.Text;
using System.Text.Json;

namespace NEXTHOS.Prompts;

public sealed class MainForm : Form
{
    private readonly WebView2 web = new() { Dock = DockStyle.Fill };
    private readonly WebView2 aiEngine = new()
    {
        Visible = false,
        Width = 2,
        Height = 2,
        Location = new Point(-20, -20)
    };

    private readonly HttpClient client;
    private TaskCompletionSource<bool>? aiNavigationReady;
    private TaskCompletionSource<AiPromptResult>? pendingAiPrompt;
    private readonly SemaphoreSlim imagePromptLock = new(1, 1);
    private const string AiOmniGenUrl = "https://aiomnigen.com/tools/image-to-prompt";

    public MainForm()
    {
        Text = "NEXTHOS PROMPTS";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1024, 680);
        Size = new Size(1440, 900);
        BackColor = Color.FromArgb(3, 4, 5);

        Controls.Add(web);
        Controls.Add(aiEngine);

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
            Timeout = TimeSpan.FromSeconds(90)
        };

        Shown += async (_, _) => await InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        var userData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NEXTHOS", "WebView2Profile");

        Directory.CreateDirectory(userData);

        var env = await CoreWebView2Environment.CreateAsync(null, userData);
        await web.EnsureCoreWebView2Async(env);
        await aiEngine.EnsureCoreWebView2Async(env);

        ConfigureWebView(web, isEngine: false);
        ConfigureWebView(aiEngine, isEngine: true);

        web.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
        web.CoreWebView2.NewWindowRequested += (_, e) => e.Handled = true;

        aiEngine.CoreWebView2.NewWindowRequested += (_, e) => e.Handled = true;
        aiEngine.CoreWebView2.NavigationCompleted += (_, e) =>
        {
            if (e.IsSuccess)
                aiNavigationReady?.TrySetResult(true);
            else
                aiNavigationReady?.TrySetException(new InvalidOperationException("Não foi possível carregar o motor de análise."));
        };
        aiEngine.CoreWebView2.WebResourceResponseReceived += OnAiWebResourceResponseReceived;

        var webRoot = Path.Combine(AppContext.BaseDirectory, "Web");
        web.CoreWebView2.SetVirtualHostNameToFolderMapping(
            "app.nexthos.local",
            webRoot,
            CoreWebView2HostResourceAccessKind.Allow);

        web.Source = new Uri("https://app.nexthos.local/index.html");

        aiNavigationReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        aiEngine.Source = new Uri(AiOmniGenUrl);
    }

    private static void ConfigureWebView(WebView2 view, bool isEngine)
    {
        var settings = view.CoreWebView2.Settings;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreDevToolsEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = !isEngine;
        settings.IsZoomControlEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsPasswordAutosaveEnabled = true;
        settings.IsGeneralAutofillEnabled = true;
        view.ZoomFactor = 1.0;
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string? messageType = null;
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeElement)) return;
            messageType = typeElement.GetString();

            if (messageType == "transcribeTikTok")
            {
                var tiktokUrl = root.TryGetProperty("url", out var u) ? u.GetString()?.Trim() : null;
                if (string.IsNullOrWhiteSpace(tiktokUrl) ||
                    !Uri.TryCreate(tiktokUrl, UriKind.Absolute, out var uri) ||
                    !(uri.Host.Equals("tiktok.com", StringComparison.OrdinalIgnoreCase) ||
                      uri.Host.EndsWith(".tiktok.com", StringComparison.OrdinalIgnoreCase)))
                {
                    await SendTranscriptionErrorAsync("Link do TikTok inválido.");
                    return;
                }

                var result = await TranscribeAsync(tiktokUrl);
                web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new
                {
                    type = "transcriptionResult",
                    data = result
                }));
                return;
            }

            if (messageType == "imageToPrompt")
            {
                var base64Url = root.TryGetProperty("base64Url", out var b) ? b.GetString() : null;
                if (string.IsNullOrWhiteSpace(base64Url) ||
                    !base64Url.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
                {
                    await SendImagePromptErrorAsync("Imagem inválida. Selecione PNG, JPG ou WEBP.");
                    return;
                }

                if (base64Url.Length > 8_500_000)
                {
                    await SendImagePromptErrorAsync("Imagem muito grande. Use uma imagem de até 4 MB.");
                    return;
                }

                var prompt = await GenerateWithAiOmniGenAsync(base64Url);
                web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new
                {
                    type = "imagePromptResult",
                    data = new
                    {
                        prompt,
                        provider = "AI OmniGen",
                        poweredBy = "AI OmniGen",
                        integration = "NEXTHOS native WebView2 engine"
                    }
                }));
            }
        }
        catch (Exception ex)
        {
            if (messageType == "imageToPrompt")
                await SendImagePromptErrorAsync(ex.Message);
            else
                await SendTranscriptionErrorAsync(ex.Message);
        }
    }

    private async Task<string> GenerateWithAiOmniGenAsync(string base64Url)
    {
        await imagePromptLock.WaitAsync();
        try
        {
            await EnsureAiEngineReadyAsync();
            pendingAiPrompt = new TaskCompletionSource<AiPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);

            var imageJson = JsonSerializer.Serialize(base64Url);
            var automationScript = $$"""
            (async () => {
              const sleep = ms => new Promise(r => setTimeout(r, ms));
              const imageData = {{imageJson}};
              const waitFor = async (fn, timeout = 20000) => {
                const started = Date.now();
                while (Date.now() - started < timeout) {
                  try { const value = fn(); if (value) return value; } catch (_) {}
                  await sleep(250);
                }
                throw new Error('Elemento do gerador não encontrado.');
              };

              const fileInput = await waitFor(() => document.querySelector('input[type="file"]'));
              const response = await fetch(imageData);
              const blob = await response.blob();
              const extension = blob.type.includes('png') ? 'png' : blob.type.includes('webp') ? 'webp' : 'jpg';
              const file = new File([blob], `nexthos-image.${extension}`, { type: blob.type || 'image/jpeg' });
              const transfer = new DataTransfer();
              transfer.items.add(file);
              const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'files')?.set;
              if (setter) setter.call(fileInput, transfer.files); else fileInput.files = transfer.files;
              fileInput.dispatchEvent(new Event('input', { bubbles: true }));
              fileInput.dispatchEvent(new Event('change', { bubbles: true }));

              for (const select of document.querySelectorAll('select')) {
                const options = [...select.options];
                let opt = options.find(o => /descriptive/i.test(o.textContent || ''));
                if (!opt) opt = options.find(o => /very long|long/i.test(o.textContent || ''));
                if (opt) {
                  select.value = opt.value;
                  select.dispatchEvent(new Event('input', { bubbles: true }));
                  select.dispatchEvent(new Event('change', { bubbles: true }));
                }
              }

              await sleep(700);
              const generate = await waitFor(() => [...document.querySelectorAll('button')].find(b =>
                /^\s*generate\s*$/i.test((b.innerText || b.textContent || '').trim()) && !b.disabled
              ), 25000);
              generate.click();
              return JSON.stringify({ ok: true });
            })();
            """;

            var jsResult = await aiEngine.ExecuteScriptAsync(automationScript);
            if (jsResult.Contains("Elemento do gerador não encontrado", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("O AI OmniGen mudou a interface e o NEXTHOS não encontrou o botão de geração.");

            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            using var registration = timeout.Token.Register(() =>
                pendingAiPrompt?.TrySetException(new TimeoutException("A análise demorou mais de 3 minutos.")));

            var result = await pendingAiPrompt.Task;
            if (!string.IsNullOrWhiteSpace(result.Error))
                throw new InvalidOperationException(result.Error);
            if (string.IsNullOrWhiteSpace(result.Prompt))
                throw new InvalidOperationException("O gerador respondeu sem texto de prompt.");

            return result.Prompt.Trim();
        }
        finally
        {
            pendingAiPrompt = null;
            imagePromptLock.Release();
        }
    }

    private async Task EnsureAiEngineReadyAsync()
    {
        if (aiEngine.CoreWebView2 == null)
            throw new InvalidOperationException("O motor de imagem ainda não foi inicializado.");

        if (aiEngine.Source?.Host.Equals("aiomnigen.com", StringComparison.OrdinalIgnoreCase) == true &&
            aiNavigationReady?.Task.IsCompletedSuccessfully == true)
            return;

        aiNavigationReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        aiEngine.Source = new Uri(AiOmniGenUrl);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var registration = timeout.Token.Register(() =>
            aiNavigationReady.TrySetException(new TimeoutException("O motor de imagem não carregou a tempo.")));
        await aiNavigationReady.Task;
    }

    private async void OnAiWebResourceResponseReceived(object? sender, CoreWebView2WebResourceResponseReceivedEventArgs e)
    {
        var pending = pendingAiPrompt;
        if (pending == null) return;

        try
        {
            var req = e.Request;
            if (!string.Equals(req.Method, "POST", StringComparison.OrdinalIgnoreCase)) return;
            if (!Uri.TryCreate(req.Uri, UriKind.Absolute, out var uri)) return;
            if (!uri.Host.Equals("aiomnigen.com", StringComparison.OrdinalIgnoreCase)) return;
            if (!uri.AbsolutePath.Contains("/tools/image-to-prompt", StringComparison.OrdinalIgnoreCase)) return;

            using var stream = await e.Response.GetContentAsync();
            if (stream == null) return;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(body)) return;

            var parsed = ParseAiOmniGenResponse(body);
            if (!string.IsNullOrWhiteSpace(parsed.Prompt) || !string.IsNullOrWhiteSpace(parsed.Error))
                pending.TrySetResult(parsed);
        }
        catch (Exception ex)
        {
            pending.TrySetException(ex);
        }
    }

    private static AiPromptResult ParseAiOmniGenResponse(string raw)
    {
        string? bestPrompt = null;
        string? error = null;

        foreach (var source in EnumerateJsonCandidates(raw))
        {
            try
            {
                using var doc = JsonDocument.Parse(source);
                WalkJson(doc.RootElement, ref bestPrompt, ref error);
            }
            catch { }
        }

        return new AiPromptResult(bestPrompt, error);
    }

    private static IEnumerable<string> EnumerateJsonCandidates(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.StartsWith("{") || trimmed.StartsWith("["))
            yield return trimmed;

        foreach (var lineRaw in raw.Split('\n'))
        {
            var line = lineRaw.Trim();
            if (line.Length == 0) continue;
            var colon = line.IndexOf(':');
            if (colon >= 0 && colon + 1 < line.Length)
            {
                var tail = line[(colon + 1)..].Trim();
                if (tail.StartsWith("{") || tail.StartsWith("[") || tail.StartsWith("\""))
                    yield return tail;
            }
        }
    }

    private static void WalkJson(JsonElement element, ref string? bestPrompt, ref string? error)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (element.TryGetProperty("success", out var success) &&
                    success.ValueKind == JsonValueKind.False &&
                    element.TryGetProperty("error", out var err) &&
                    err.ValueKind == JsonValueKind.String)
                {
                    error ??= FriendlyAiError(err.GetString() ?? "Falha no gerador.");
                }

                foreach (var property in element.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String && IsPromptField(property.Name))
                    {
                        var value = property.Value.GetString()?.Trim();
                        if (LooksLikePrompt(value) && (bestPrompt == null || value!.Length > bestPrompt.Length))
                            bestPrompt = value;
                    }
                    WalkJson(property.Value, ref bestPrompt, ref error);
                }
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    WalkJson(item, ref bestPrompt, ref error);
                break;

            case JsonValueKind.String:
                var text = element.GetString()?.Trim();
                if (LooksLikePrompt(text) && (bestPrompt == null || text!.Length > bestPrompt.Length))
                    bestPrompt = text;
                break;
        }
    }

    private static bool IsPromptField(string name) =>
        name.Equals("data", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("prompt", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("description", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("caption", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("result", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("text", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("content", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikePrompt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < 80) return false;
        if (value.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return false;
        if (value.Contains("next-action", StringComparison.OrdinalIgnoreCase)) return false;
        if (value.Contains("ZeroGPU runs limit", StringComparison.OrdinalIgnoreCase)) return false;
        if (value.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)) return false;
        return value.Count(char.IsWhiteSpace) >= 8;
    }

    private static string FriendlyAiError(string error)
    {
        if (error.Contains("ZeroGPU runs limit", StringComparison.OrdinalIgnoreCase))
            return "O serviço de geração atingiu uma cota temporária. Tente novamente mais tarde.";
        return error;
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
        request.Headers.Accept.ParseAdd("*/*");
        request.Headers.AcceptLanguage.ParseAdd("pt-BR,pt;q=0.9,en-US;q=0.8,en;q=0.7");
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/151.0.0.0 Safari/537.36");
        request.Headers.Referrer = new Uri("https://submagic-free-tools.fly.dev/tiktok-transcription");
        request.Headers.TryAddWithoutValidation("Origin", "https://submagic-free-tools.fly.dev");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Erro HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {body}");

        using var parsed = JsonDocument.Parse(body);
        if (!parsed.RootElement.TryGetProperty("transcripts", out _))
            throw new InvalidOperationException("A resposta não contém transcrição.");

        return parsed.RootElement.Clone();
    }

    private Task SendTranscriptionErrorAsync(string message)
    {
        if (web.CoreWebView2 == null) return Task.CompletedTask;
        web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "transcriptionError", message }));
        return Task.CompletedTask;
    }

    private Task SendImagePromptErrorAsync(string message)
    {
        if (web.CoreWebView2 == null) return Task.CompletedTask;
        web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "imagePromptError", message }));
        return Task.CompletedTask;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            client.Dispose();
            imagePromptLock.Dispose();
            aiEngine.Dispose();
            web.Dispose();
        }
        base.Dispose(disposing);
    }

    private sealed record AiPromptResult(string? Prompt, string? Error);
}
