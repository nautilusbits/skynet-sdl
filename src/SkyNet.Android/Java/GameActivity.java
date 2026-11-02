package net.skynetport.skynet;

import android.content.Context;
import android.net.wifi.WifiManager;
import android.os.Bundle;
import android.view.WindowManager;
import org.libsdl.app.SDL;
import org.libsdl.app.SDLActivity;

/**
 * The game under NativeAOT: SDL's activity in Java (a C# subclass of the SDL binding needs
 * Reflection.Emit, which NativeAOT lacks). SDL runs SDL_main from libSkyNet.so (GameActivity.cs)
 * with getArguments(): the user root, then the data folder SetupActivity found.
 */
public class GameActivity extends SDLActivity {
    private static final String MULTICAST_TAG = "SkyNET LAN games";
    private static final String DATA_EXTRA = "data";
    private static final String ROOT_EXTRA = "root";

    // Held while the game runs: without it many phones drop the LAN list's broadcasts.
    private WifiManager.MulticastLock multicast;

    @Override
    protected String[] getLibraries() {
        return new String[] { "SDL3", "SkyNet" };
    }

    @Override
    protected String[] getArguments() {
        String root = getIntent().getStringExtra(ROOT_EXTRA);
        if (root == null) {
            // No shared storage mounted: the internal folder, as DataSetup's.
            java.io.File external = getExternalFilesDir(null);
            root = (external != null ? external : getFilesDir()).getAbsolutePath();
        }

        String data = getIntent().getStringExtra(DATA_EXTRA);
        return new String[] { root, data != null ? data : root };
    }

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);

        // The screen stays on while the game shows: a phone in a headset gets no touches to keep it awake.
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);

        WifiManager wifi = (WifiManager) getApplicationContext().getSystemService(Context.WIFI_SERVICE);
        if (wifi != null) {
            multicast = wifi.createMulticastLock(MULTICAST_TAG);
            multicast.acquire();
        }
    }

    @Override
    protected void onDestroy() {
        if (multicast != null && multicast.isHeld()) {
            multicast.release();
        }

        super.onDestroy();
    }

    /** The window's own brightness (0..1; -1: the system's), called by the game from SDL's thread. */
    public static void setBrightness(final float level) {
        final android.app.Activity activity = SDL.getContext();
        if (activity == null) {
            return;
        }

        activity.runOnUiThread(() -> {
            WindowManager.LayoutParams attributes = activity.getWindow().getAttributes();
            attributes.screenBrightness = level;
            activity.getWindow().setAttributes(attributes);
        });
    }
}
