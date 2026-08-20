package club.nexthos.prompts;

import android.annotation.SuppressLint;
import android.app.Activity;
import android.os.Bundle;
import android.view.View;
import android.view.Window;
import android.webkit.JavascriptInterface;
import android.webkit.WebChromeClient;
import android.webkit.WebResourceRequest;
import android.webkit.WebSettings;
import android.webkit.WebView;
import android.webkit.WebViewClient;

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
    private WebView webView;
    private final ExecutorService executor = Executors.newSingleThreadExecutor();

    @SuppressLint({"SetJavaScriptEnabled", "JavascriptInterface"})
    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        requestWindowFeature(Window.FEATURE_NO_TITLE);

        webView = new WebView(this);
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
        s.setUserAgentString(s.getUserAgentString() + " NEXTHOS-Mobile/21.0");

        webView.setOverScrollMode(View.OVER_SCROLL_NEVER);
        webView.setLongClickable(false);
        webView.setHapticFeedbackEnabled(false);
        webView.addJavascriptInterface(new AndroidBridge(), "NEXTHOS_ANDROID");
        webView.setWebChromeClient(new WebChromeClient());
        webView.setWebViewClient(new WebViewClient() {
            @Override
            public boolean shouldOverrideUrlLoading(WebView view, WebResourceRequest request) {
                return false;
            }
        });

        webView.loadUrl("file:///android_asset/index.html");
    }

    public class AndroidBridge {
        @JavascriptInterface
        public void transcribeTikTok(String tiktokUrl) {
            executor.execute(() -> doTranscription(tiktokUrl));
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

            JSONObject parsed = new JSONObject(response);
            JSONObject message = new JSONObject();
            message.put("type", "transcriptionResult");
            message.put("data", parsed);
            sendToJs(message);
        } catch (Exception e) {
            try {
                JSONObject message = new JSONObject();
                message.put("type", "transcriptionError");
                message.put("message", e.getMessage() == null ? "Erro na transcrição" : e.getMessage());
                sendToJs(message);
            } catch (Exception ignored) {}
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
        runOnUiThread(() -> webView.evaluateJavascript("window.__nexthosAndroidMessage(" + raw + ");", null));
    }

    @Override
    public void onBackPressed() {
        webView.evaluateJavascript("document.getElementById('promptDrawer')?.classList.contains('open')", value -> {
            if ("true".equals(value)) {
                webView.evaluateJavascript("document.getElementById('drawerClose')?.click()", null);
            } else if (webView.canGoBack()) {
                webView.goBack();
            } else {
                MainActivity.super.onBackPressed();
            }
        });
    }

    @Override
    protected void onDestroy() {
        executor.shutdownNow();
        if (webView != null) {
            webView.destroy();
            webView = null;
        }
        super.onDestroy();
    }
}
