package club.nexthos.prompts;

import android.annotation.SuppressLint;
import android.app.Activity;
import android.content.ActivityNotFoundException;
import android.content.ClipData;
import android.content.Intent;
import android.graphics.Color;
import android.net.Uri;
import android.os.Bundle;
import android.view.View;
import android.view.Window;
import android.webkit.JavascriptInterface;
import android.webkit.ValueCallback;
import android.webkit.WebChromeClient;
import android.webkit.WebResourceRequest;
import android.webkit.WebSettings;
import android.webkit.WebView;
import android.webkit.WebViewClient;
import android.widget.Toast;

import org.json.JSONObject;

import java.io.BufferedReader;
import java.io.InputStream;
import java.io.InputStreamReader;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

public class MainActivity extends Activity {
    private static final int FILE_CHOOSER_REQUEST = 4401;
    private WebView webView;
    private ValueCallback<Uri[]> pendingFileCallback;
    private final ExecutorService executor = Executors.newSingleThreadExecutor();

    @SuppressLint({"SetJavaScriptEnabled", "JavascriptInterface"})
    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        requestWindowFeature(Window.FEATURE_NO_TITLE);

        Window window = getWindow();
        window.setStatusBarColor(Color.rgb(7, 8, 11));
        window.setNavigationBarColor(Color.rgb(7, 8, 11));
        if (android.os.Build.VERSION.SDK_INT >= 23) {
            window.getDecorView().setSystemUiVisibility(0);
        }

        webView = new WebView(this);
        webView.setBackgroundColor(Color.rgb(7, 8, 11));
        setContentView(webView);

        WebSettings s = webView.getSettings();
        s.setJavaScriptEnabled(true);
        s.setDomStorageEnabled(true);
        s.setDatabaseEnabled(true);
        s.setAllowFileAccess(true);
        s.setAllowContentAccess(true);
        s.setBuiltInZoomControls(false);
        s.setSupportZoom(false);
        s.setDisplayZoomControls(false);
        s.setMediaPlaybackRequiresUserGesture(false);
        s.setMixedContentMode(WebSettings.MIXED_CONTENT_COMPATIBILITY_MODE);
        s.setTextZoom(100);
        s.setUserAgentString(s.getUserAgentString() + " NEXTHOS-Mobile/22.0");

        webView.setOverScrollMode(View.OVER_SCROLL_NEVER);
        webView.setLongClickable(true);
        webView.setHapticFeedbackEnabled(true);
        webView.addJavascriptInterface(new AndroidBridge(), "NEXTHOS_ANDROID");

        webView.setWebChromeClient(new WebChromeClient() {
            @Override
            public boolean onShowFileChooser(WebView webView, ValueCallback<Uri[]> filePathCallback, FileChooserParams fileChooserParams) {
                if (pendingFileCallback != null) pendingFileCallback.onReceiveValue(null);
                pendingFileCallback = filePathCallback;
                try {
                    Intent intent = new Intent(Intent.ACTION_OPEN_DOCUMENT);
                    intent.addCategory(Intent.CATEGORY_OPENABLE);
                    intent.setType("*/*");
                    intent.putExtra(Intent.EXTRA_ALLOW_MULTIPLE, fileChooserParams.getMode() == FileChooserParams.MODE_OPEN_MULTIPLE);
                    String[] accept = fileChooserParams.getAcceptTypes();
                    if (accept != null && accept.length > 0) intent.putExtra(Intent.EXTRA_MIME_TYPES, accept);
                    startActivityForResult(Intent.createChooser(intent, "Selecionar arquivo"), FILE_CHOOSER_REQUEST);
                    return true;
                } catch (Exception e) {
                    pendingFileCallback = null;
                    Toast.makeText(MainActivity.this, "Não foi possível abrir os arquivos.", Toast.LENGTH_SHORT).show();
                    return false;
                }
            }
        });

        webView.setWebViewClient(new WebViewClient() {
            @Override
            public boolean shouldOverrideUrlLoading(WebView view, WebResourceRequest request) {
                Uri uri = request.getUrl();
                String scheme = uri.getScheme();
                if ("http".equalsIgnoreCase(scheme) || "https".equalsIgnoreCase(scheme)) {
                    openExternal(uri.toString());
                    return true;
                }
                return false;
            }
        });

        webView.loadUrl("file:///android_asset/index.html");
    }

    public class AndroidBridge {
        @JavascriptInterface
        public void openExternal(String url) {
            runOnUiThread(() -> MainActivity.this.openExternal(url));
        }

        @JavascriptInterface
        public void shareLink(String title, String url) {
            runOnUiThread(() -> {
                Intent share = new Intent(Intent.ACTION_SEND);
                share.setType("text/plain");
                share.putExtra(Intent.EXTRA_SUBJECT, title == null ? "NEXTHOS" : title);
                share.putExtra(Intent.EXTRA_TEXT, url == null ? "" : url);
                startActivity(Intent.createChooser(share, "Compartilhar agente"));
            });
        }

        @JavascriptInterface
        public void transcribeTikTok(String tiktokUrl) {
            executor.execute(() -> doTranscription(tiktokUrl));
        }
    }

    private void openExternal(String rawUrl) {
        try {
            Uri uri = Uri.parse(rawUrl);
            String scheme = uri.getScheme();
            if (!"http".equalsIgnoreCase(scheme) && !"https".equalsIgnoreCase(scheme)) return;
            Intent intent = new Intent(Intent.ACTION_VIEW, uri);
            intent.addCategory(Intent.CATEGORY_BROWSABLE);
            startActivity(intent);
        } catch (ActivityNotFoundException e) {
            Toast.makeText(this, "Nenhum aplicativo disponível para abrir este link.", Toast.LENGTH_LONG).show();
        } catch (Exception e) {
            Toast.makeText(this, "Não foi possível abrir o link.", Toast.LENGTH_SHORT).show();
        }
    }

    private void doTranscription(String tiktokUrl) {
        HttpURLConnection conn = null;
        try {
            URL endpoint = new URL("https://submagic-free-tools.fly.dev/api/tiktok-transcription");
            conn = (HttpURLConnection) endpoint.openConnection();
            conn.setRequestMethod("POST");
            conn.setConnectTimeout(15000);
            conn.setReadTimeout(30000);
            conn.setDoOutput(true);
            conn.setRequestProperty("Accept", "*/*");
            conn.setRequestProperty("Accept-Language", "pt-BR,pt;q=0.9,en-US;q=0.8,en;q=0.7");
            conn.setRequestProperty("Content-Type", "application/json");
            conn.setRequestProperty("Origin", "https://submagic-free-tools.fly.dev");
            conn.setRequestProperty("Referer", "https://submagic-free-tools.fly.dev/tiktok-transcription");
            conn.setRequestProperty("User-Agent", "Mozilla/5.0 (Linux; Android 15) AppleWebKit/537.36 Chrome/151.0.0.0 Mobile Safari/537.36");

            JSONObject body = new JSONObject();
            body.put("url", tiktokUrl);
            byte[] bytes = body.toString().getBytes(StandardCharsets.UTF_8);
            conn.setFixedLengthStreamingMode(bytes.length);
            try (OutputStream os = conn.getOutputStream()) { os.write(bytes); }

            int status = conn.getResponseCode();
            InputStream input = status >= 200 && status < 300 ? conn.getInputStream() : conn.getErrorStream();
            String response = readAll(input);
            if (status < 200 || status >= 300) throw new Exception("Erro HTTP " + status + ": " + response);

            JSONObject message = new JSONObject();
            message.put("type", "transcriptionResult");
            message.put("data", new JSONObject(response));
            sendToJs(message);
        } catch (Exception e) {
            try {
                JSONObject message = new JSONObject();
                message.put("type", "transcriptionError");
                message.put("message", e.getMessage() == null ? "Erro na transcrição" : e.getMessage());
                sendToJs(message);
            } catch (Exception ignored) { }
        } finally {
            if (conn != null) conn.disconnect();
        }
    }

    private String readAll(InputStream input) throws Exception {
        if (input == null) return "";
        StringBuilder sb = new StringBuilder();
        try (BufferedReader br = new BufferedReader(new InputStreamReader(input, StandardCharsets.UTF_8))) {
            String line;
            while ((line = br.readLine()) != null) sb.append(line);
        }
        return sb.toString();
    }

    private void sendToJs(JSONObject message) {
        final String raw = JSONObject.quote(message.toString());
        runOnUiThread(() -> {
            if (webView != null) webView.evaluateJavascript("window.__nexthosAndroidMessage(" + raw + ");", null);
        });
    }

    @Override
    protected void onActivityResult(int requestCode, int resultCode, Intent data) {
        super.onActivityResult(requestCode, resultCode, data);
        if (requestCode != FILE_CHOOSER_REQUEST || pendingFileCallback == null) return;
        Uri[] results = null;
        if (resultCode == RESULT_OK && data != null) {
            ClipData clip = data.getClipData();
            if (clip != null) {
                results = new Uri[clip.getItemCount()];
                for (int i = 0; i < clip.getItemCount(); i++) results[i] = clip.getItemAt(i).getUri();
            } else if (data.getData() != null) {
                results = new Uri[]{data.getData()};
            }
        }
        pendingFileCallback.onReceiveValue(results);
        pendingFileCallback = null;
    }

    @Override
    public void onBackPressed() {
        if (webView == null) { super.onBackPressed(); return; }
        webView.evaluateJavascript("window.__nexthosAndroidBack ? window.__nexthosAndroidBack() : false", value -> {
            if ("true".equals(value)) return;
            if (webView.canGoBack()) webView.goBack(); else MainActivity.super.onBackPressed();
        });
    }

    @Override
    protected void onDestroy() {
        executor.shutdownNow();
        if (webView != null) {
            webView.removeJavascriptInterface("NEXTHOS_ANDROID");
            webView.destroy();
            webView = null;
        }
        super.onDestroy();
    }
}
