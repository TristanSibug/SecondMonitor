// ============================================================================
// MainActivity  (Tablet side)  --  H.264 version
//
// What this app does, in plain English:
//   1. Opens full screen and keeps the screen awake.
//   2. In the background it keeps trying to connect to 127.0.0.1:7777.
//      (Because of "adb reverse", that address secretly leads to the PC
//       through the USB cable.)
//   3. When connected, the PC first tells us the video size, then sends
//      pictures as:  [4 bytes = size][one H.264 picture].
//   4. We feed each picture to the tablet's video decoder (MediaCodec).
//      The decoder draws the result straight onto the screen (the Surface).
//   5. When you touch the screen, we send "D/M/U x y" lines back to the PC
//      so the PC can move the mouse / click.
// ============================================================================
package com.example.secondmonitor;

import android.app.Activity;
import android.media.MediaCodec;
import android.media.MediaFormat;
import android.os.Bundle;
import android.view.MotionEvent;
import android.view.SurfaceHolder;
import android.view.SurfaceView;
import android.view.View;
import android.view.WindowManager;

import java.io.BufferedInputStream;
import java.io.DataInputStream;
import java.io.IOException;
import java.io.OutputStream;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.nio.ByteBuffer;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

public class MainActivity extends Activity implements SurfaceHolder.Callback {

    // Must match the port in the PC program and in "adb reverse"
    private static final int PORT = 7777;

    // The view the decoder draws video on
    private SurfaceView surfaceView;

    // true while the app is in front; false tells the background thread to stop
    private volatile boolean running = false;

    // Each time the app comes to the front we start a new background thread.
    // This number lets an OLD thread notice "I am outdated, stop".
    private volatile int generation = 0;

    // true only while the drawing surface actually exists (safe to use)
    private volatile boolean surfaceReady = false;

    // The connection's "output pipe" (used to send touches). null = not connected.
    private volatile OutputStream outStream = null;

    // The background thread that does all the networking
    private Thread networkThread;

    // Runs small jobs on ONE other thread. Android does not allow network work
    // on the main thread, so touches go through this.
    private final ExecutorService sendExecutor = Executors.newSingleThreadExecutor();

    // ------------------------------------------------------------------
    // onCreate: runs once when the app starts
    // ------------------------------------------------------------------
    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);

        // Keep the screen from turning off while we are a monitor
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);

        // Make a SurfaceView and make it the whole screen content
        surfaceView = new SurfaceView(this);
        setContentView(surfaceView);

        // Ask to be told when the surface is created/destroyed (see methods below)
        surfaceView.getHolder().addCallback(this);

        // When the finger touches the view, run our touch code
        surfaceView.setOnTouchListener(new View.OnTouchListener() {
            @Override
            public boolean onTouch(View v, MotionEvent event) {
                handleTouch(event);
                return true; // true = "I used this touch"
            }
        });
    }

    // Hide the status bar and navigation buttons (full screen "immersive" mode)
    private void goFullScreen() {
        surfaceView.setSystemUiVisibility(
                View.SYSTEM_UI_FLAG_FULLSCREEN
                | View.SYSTEM_UI_FLAG_HIDE_NAVIGATION
                | View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY
                | View.SYSTEM_UI_FLAG_LAYOUT_STABLE
                | View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN
                | View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION);
    }

    // onResume: runs when the app comes to the front. Start networking here.
    @Override
    protected void onResume() {
        super.onResume();
        goFullScreen();

        running = true;
        generation++;                     // a new "version" of the background thread
        final int myGeneration = generation;

        networkThread = new Thread(new Runnable() {
            @Override
            public void run() {
                networkLoop(myGeneration);
            }
        });
        networkThread.start();
    }

    // onPause: runs when the app leaves the front. Stop networking.
    @Override
    protected void onPause() {
        super.onPause();
        running = false; // tells networkLoop to finish
        if (networkThread != null) {
            networkThread.interrupt();
        }
    }

    // Is the background thread with this generation number still wanted?
    private boolean isActive(int myGeneration) {
        return running && myGeneration == generation;
    }

    // ------------------------------------------------------------------
    // networkLoop: connect, read pictures, decode them. Repeat forever.
    // ------------------------------------------------------------------
    private void networkLoop(int myGeneration) {
        // A reusable memory area for one incoming picture
        byte[] buffer = new byte[512 * 1024];

        // Safety net: if the PC was connected and then disappears (shut down, program closed,
        // cable pulled) and does not come back, close the app instead of staying frozen.
        boolean everConnected = false; // true once we have connected to the PC at least once
        long lostSince = 0;            // when the connection was lost (0 = not lost right now)

        while (isActive(myGeneration)) {
            Socket socket = null;
            MediaCodec decoder = null;
            Thread outputThread = null;

            try {
                // Wait until the drawing surface exists
                while (isActive(myGeneration) && !surfaceReady) {
                    Thread.sleep(50);
                }
                if (!isActive(myGeneration)) return;

                // Try to connect (wait at most 2 seconds)
                socket = new Socket();
                socket.connect(new InetSocketAddress("127.0.0.1", PORT), 2000);
                socket.setTcpNoDelay(true); // send touches immediately
                everConnected = true;       // we reached the PC
                lostSince = 0;

                // Remember the output pipe so touches can use it
                outStream = socket.getOutputStream();

                // Wrap the input so we can use readInt() and readFully()
                DataInputStream in = new DataInputStream(
                        new BufferedInputStream(socket.getInputStream(), 256 * 1024));

                // The PC first tells us the video size
                int videoWidth = in.readInt();
                int videoHeight = in.readInt();

                long pictureNumber = 0;

                // Keep reading pictures until something breaks
                while (isActive(myGeneration)) {
                    // 1) read the 4-byte size
                    int length = in.readInt();

                    // A size of -1 is the PC saying goodbye (it is shutting down or was told
                    // to close us). Stop for good and close the app.
                    if (length == -1) {
                        closeApp();
                        return;
                    }

                    // Sanity check so a bad number cannot crash us
                    if (length <= 0 || length > 20 * 1024 * 1024) {
                        throw new IOException("Bad picture size: " + length);
                    }

                    // 2) if our buffer is too small, make a bigger one
                    if (length > buffer.length) {
                        buffer = new byte[length];
                    }

                    // 3) read exactly 'length' bytes (one H.264 picture)
                    in.readFully(buffer, 0, length);

                    // 4) the first picture contains the decoder settings (SPS/PPS).
                    //    Use them to create and start the decoder.
                    if (decoder == null) {
                        decoder = createDecoder(buffer, length, videoWidth, videoHeight);
                        outputThread = startOutputThread(decoder, myGeneration);
                    }

                    // 5) give the picture to the decoder
                    feedDecoder(decoder, buffer, length, pictureNumber, myGeneration);
                    pictureNumber++;
                }
            } catch (Exception e) {
                // Could not connect, the cable was unplugged, or the decoder failed.
                // Just fall through and try again in a moment.
            } finally {
                outStream = null;

                // Stop the thread that collects decoded pictures
                if (outputThread != null) {
                    outputThread.interrupt();
                    try { outputThread.join(300); } catch (InterruptedException ignored) { }
                }

                // Shut down and free the decoder
                if (decoder != null) {
                    try { decoder.stop(); } catch (Exception ignored) { }
                    try { decoder.release(); } catch (Exception ignored) { }
                }

                if (socket != null) {
                    try { socket.close(); } catch (IOException ignored) { }
                }
            }

            // The connection is gone. Note when that started; if the PC does not come back
            // within 10 seconds, close the app.
            if (everConnected) {
                long now = System.currentTimeMillis();
                if (lostSince == 0) lostSince = now;
                if (now - lostSince > 10000) {
                    closeApp();
                    return;
                }
            }

            // Wait one second before trying to connect again
            try {
                Thread.sleep(1000);
            } catch (InterruptedException e) {
                return; // we were asked to stop
            }
        }
    }

    // ------------------------------------------------------------------
    // closeApp: stops networking and closes the app (also removes it from the recent-apps list)
    // ------------------------------------------------------------------
    private void closeApp() {
        running = false;
        runOnUiThread(new Runnable() {
            @Override
            public void run() {
                finishAndRemoveTask();
            }
        });
    }

    // ------------------------------------------------------------------
    // createDecoder: builds the hardware H.264 decoder.
    // We look inside the first picture for two special blocks:
    //   SPS and PPS = "how this video is set up". We hand them to the decoder up front.
    // ------------------------------------------------------------------
    private MediaCodec createDecoder(byte[] picture, int length, int width, int height)
            throws IOException {

        MediaFormat format = MediaFormat.createVideoFormat("video/avc", width, height);

        // Allow big pictures (the first full picture can be large)
        format.setInteger(MediaFormat.KEY_MAX_INPUT_SIZE, 1024 * 1024);

        byte[] sps = null;
        byte[] pps = null;

        // Walk through the picture looking for "start codes" (00 00 01) that begin each block.
        int i = 0;
        while (i + 3 < length) {
            if (picture[i] == 0 && picture[i + 1] == 0 && picture[i + 2] == 1) {
                int nalStart = i + 3; // first byte of the block (its header)

                // Find where this block ends = where the next start code begins
                int j = nalStart;
                boolean found = false;
                while (j + 2 < length) {
                    if (picture[j] == 0 && picture[j + 1] == 0 && picture[j + 2] == 1) {
                        found = true;
                        break;
                    }
                    j++;
                }
                int nalEnd = found ? j : length;

                // Cut off zero bytes that belong to the next 4-byte start code
                while (nalEnd > nalStart && picture[nalEnd - 1] == 0) {
                    nalEnd--;
                }

                // The low 5 bits of the header say what kind of block it is: 7 = SPS, 8 = PPS
                int type = picture[nalStart] & 0x1F;
                if (type == 7 && sps == null) {
                    sps = withStartCode(picture, nalStart, nalEnd);
                } else if (type == 8 && pps == null) {
                    pps = withStartCode(picture, nalStart, nalEnd);
                }

                i = nalEnd; // continue after this block
            } else {
                i++;
            }
        }

        // Give them to the decoder under the names it expects
        if (sps != null) format.setByteBuffer("csd-0", ByteBuffer.wrap(sps));
        if (pps != null) format.setByteBuffer("csd-1", ByteBuffer.wrap(pps));

        // Create the decoder and tell it to draw directly onto our SurfaceView
        MediaCodec codec = MediaCodec.createDecoderByType("video/avc");
        codec.configure(format, surfaceView.getHolder().getSurface(), null, 0);
        codec.start();
        return codec;
    }

    // Copies one block and puts the 4-byte start code (00 00 00 01) in front of it
    private static byte[] withStartCode(byte[] data, int from, int to) {
        byte[] out = new byte[4 + (to - from)];
        out[3] = 1;
        System.arraycopy(data, from, out, 4, to - from);
        return out;
    }

    // ------------------------------------------------------------------
    // feedDecoder: hands one picture's bytes to the decoder
    // ------------------------------------------------------------------
    private void feedDecoder(MediaCodec decoder, byte[] data, int length,
                             long pictureNumber, int myGeneration) {
        // Ask the decoder for an empty input slot. If it is full, wait a bit and ask again.
        int slot;
        do {
            slot = decoder.dequeueInputBuffer(10000); // wait up to 10 ms
        } while (slot < 0 && isActive(myGeneration));
        if (slot < 0) return;

        ByteBuffer input = decoder.getInputBuffer(slot);
        input.clear();
        input.put(data, 0, length);

        // The "time stamp" (in microseconds) just counts up; we draw as soon as ready anyway.
        decoder.queueInputBuffer(slot, 0, length, pictureNumber * 16667L, 0);
    }

    // ------------------------------------------------------------------
    // startOutputThread: a helper thread that takes finished pictures out of
    // the decoder and shows them on screen.
    // ------------------------------------------------------------------
    private Thread startOutputThread(final MediaCodec decoder, final int myGeneration) {
        Thread thread = new Thread(new Runnable() {
            @Override
            public void run() {
                MediaCodec.BufferInfo info = new MediaCodec.BufferInfo();

                while (isActive(myGeneration) && !Thread.currentThread().isInterrupted()) {
                    try {
                        // Wait up to 10 ms for a finished picture
                        int ready = decoder.dequeueOutputBuffer(info, 10000);
                        if (ready < 0) continue; // nothing yet (or a "format changed" note)

                        // If more finished pictures are ALREADY waiting, we are running behind.
                        // Skip drawing the older ones and show only the newest (less lag).
                        while (true) {
                            int next = decoder.dequeueOutputBuffer(info, 0); // 0 = don't wait
                            if (next < 0) break;
                            decoder.releaseOutputBuffer(ready, false); // false = don't draw it
                            ready = next;
                        }

                        // true = draw this picture on the screen right now
                        decoder.releaseOutputBuffer(ready, true);
                    } catch (Exception e) {
                        return; // decoder was stopped or released
                    }
                }
            }
        });
        thread.start();
        return thread;
    }

    // Adds one finger's line to the text we are about to send
    private void addTouchLine(StringBuilder out, String code, MotionEvent event, int index) {
        float fx = event.getX(index) / surfaceView.getWidth();
        float fy = event.getY(index) / surfaceView.getHeight();

        // Keep the numbers inside 0..1 even if the finger is at the very edge
        fx = Math.max(0f, Math.min(1f, fx));
        fy = Math.max(0f, Math.min(1f, fy));

        // Locale.US so it always uses "." not ","
        out.append("T ").append(code).append(' ').append(event.getPointerId(index)).append(' ')
           .append(String.format(java.util.Locale.US, "%.4f %.4f", fx, fy)).append('\n');
    }

    // ------------------------------------------------------------------
    // handleTouch: turn a finger event into a text line for the PC
    // ------------------------------------------------------------------
    private void handleTouch(MotionEvent event) {
        // Every finger has its own number (pointer id). We send one line per
        // finger that changed:  "T <D|M|U> <fingerNumber> <x> <y>"
        // D = went down, M = moved, U = lifted. x and y are 0.0 - 1.0.
        int action = event.getActionMasked();
        StringBuilder lines = new StringBuilder();

        if (action == MotionEvent.ACTION_DOWN || action == MotionEvent.ACTION_POINTER_DOWN) {
            addTouchLine(lines, "D", event, event.getActionIndex());
        } else if (action == MotionEvent.ACTION_UP || action == MotionEvent.ACTION_POINTER_UP) {
            addTouchLine(lines, "U", event, event.getActionIndex());
        } else if (action == MotionEvent.ACTION_MOVE) {
            // a move event carries ALL fingers at once
            for (int i = 0; i < event.getPointerCount(); i++) {
                addTouchLine(lines, "M", event, i);
            }
        } else if (action == MotionEvent.ACTION_CANCEL) {
            // everything is cancelled: lift all fingers
            for (int i = 0; i < event.getPointerCount(); i++) {
                addTouchLine(lines, "U", event, i);
            }
        } else {
            return;
        }

        final String line = lines.toString();

        // Send it on the helper thread (network is not allowed on the main thread)
        sendExecutor.execute(new Runnable() {
            @Override
            public void run() {
                OutputStream out = outStream; // copy so it cannot become null mid-way
                if (out == null) return;      // not connected, drop the touch
                try {
                    out.write(line.getBytes("US-ASCII"));
                    out.flush();
                } catch (IOException ignored) {
                    // connection probably dropped; networkLoop will reconnect
                }
            }
        });
    }

    // ------------------------------------------------------------------
    // SurfaceHolder.Callback methods: Android tells us about the drawing surface
    // ------------------------------------------------------------------
    @Override
    public void surfaceCreated(SurfaceHolder holder) {
        surfaceReady = true; // safe to use now
    }

    @Override
    public void surfaceChanged(SurfaceHolder holder, int format, int width, int height) {
        // The video is stretched to fill the view automatically, nothing to do.
    }

    @Override
    public void surfaceDestroyed(SurfaceHolder holder) {
        surfaceReady = false; // NOT safe to use anymore
    }

    // Clean up the helper thread when the app is destroyed
    @Override
    protected void onDestroy() {
        super.onDestroy();
        sendExecutor.shutdownNow();
    }
}
