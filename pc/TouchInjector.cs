// ============================================================================
// TouchInjector
//
// Gives Windows REAL touch input, as if a touchscreen was plugged in. That is
// what makes swiping scroll a list, pinching zoom, and long-press right-click,
// instead of just moving the mouse cursor.
//
// How it works, in plain English:
//   - Every finger on the tablet has a number (0, 1, 2...).
//   - When a finger goes down / moves / lifts, the engine calls Handle().
//   - We remember which fingers are currently down, and every time something
//     changes we tell Windows about ALL fingers that are down (Windows wants
//     the full picture each time).
//
// Uses Windows' "touch injection" feature (Windows 8 or newer).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices; // lets us call Windows functions (DllImport)

class TouchInjector
{
    // ------------------------------------------------------------------
    // The structures Windows wants. The order and types of the fields must
    // match Windows' own layout EXACTLY, so do not rearrange them.
    // ------------------------------------------------------------------
    [StructLayout(LayoutKind.Sequential)]
    struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct POINTER_INFO
    {
        public uint pointerType;
        public uint pointerId;
        public uint frameId;
        public uint pointerFlags;
        public IntPtr sourceDevice;
        public IntPtr hwndTarget;
        public POINT ptPixelLocation;
        public POINT ptHimetricLocation;
        public POINT ptPixelLocationRaw;
        public POINT ptHimetricLocationRaw;
        public uint dwTime;
        public uint historyCount;
        public int inputData;
        public uint dwKeyStates;
        public ulong performanceCount;
        public uint buttonChangeType;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct POINTER_TOUCH_INFO
    {
        public POINTER_INFO pointerInfo;
        public uint touchFlags;
        public uint touchMask;
        public RECT rcContact;
        public RECT rcContactRaw;
        public uint orientation;
        public uint pressure;
    }

    // ------------------------------------------------------------------
    // Windows functions from user32.dll
    // ------------------------------------------------------------------

    // "Get ready to accept injected touches" (up to maxCount fingers at once)
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool InitializeTouchInjection(uint maxCount, uint dwMode);

    // "Here are the fingers that are down right now"
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool InjectTouchInput(uint count, [In] POINTER_TOUCH_INFO[] contacts);

    // ------------------------------------------------------------------
    // Numbers Windows understands
    // ------------------------------------------------------------------
    const uint PT_TOUCH = 0x00000002;                 // "this pointer is a finger"
    const uint POINTER_FLAG_INRANGE = 0x00000002;     // finger is near/on the screen
    const uint POINTER_FLAG_INCONTACT = 0x00000004;   // finger is touching the screen
    const uint POINTER_FLAG_DOWN = 0x00010000;        // finger just touched down
    const uint POINTER_FLAG_UPDATE = 0x00020000;      // finger moved (or stayed)
    const uint POINTER_FLAG_UP = 0x00040000;          // finger just lifted
    const uint TOUCH_FEEDBACK_DEFAULT = 0x1;          // show Windows' touch circles if the user has them on
    const uint TOUCH_MASK_CONTACTAREA = 0x1;          // the contact area fields are filled in
    const uint TOUCH_MASK_ORIENTATION = 0x2;          // the orientation field is filled in
    const uint TOUCH_MASK_PRESSURE = 0x4;             // the pressure field is filled in
    const int MaxContacts = 10;                       // Windows allows up to 10 fingers

    // ------------------------------------------------------------------
    // What we remember
    // ------------------------------------------------------------------
    readonly object sync = new object();                                 // so two threads never use us at once
    readonly Dictionary<int, POINT> active = new Dictionary<int, POINT>(); // finger number -> where it is now
    bool ready = false;                                                  // true after a successful Init()
    public int LastError = 0;                                            // the last Windows error number

    // ==================================================================
    // Init: get Windows ready. Returns true if real touch can be used.
    // 'message' says what happened (for the log).
    // ==================================================================
    public bool Init(out string message)
    {
        if (!InitializeTouchInjection((uint)MaxContacts, TOUCH_FEEDBACK_DEFAULT))
        {
            LastError = Marshal.GetLastWin32Error();
            message = "Real touchscreen input is not available (Windows error " + LastError + ").";
            return false;
        }

        ready = true;
        message = "Real touchscreen input is on (swipe to scroll, pinch to zoom).";
        return true;
    }

    // ==================================================================
    // Handle: one finger did something.
    //   action: 'D' = went down, 'M' = moved, 'U' = lifted
    //   id:     the finger's number
    //   x, y:   where it is, in pixels on the whole Windows desktop
    // Returns false if Windows refused (then the caller should switch to mouse mode).
    // ==================================================================
    public bool Handle(char action, int id, int x, int y)
    {
        lock (sync)
        {
            if (!ready) return false;

            POINT where = new POINT();
            where.X = x;
            where.Y = y;

            // What is happening to THIS finger (the others just "stay where they are")
            uint changedFlags;

            if (action == 'D')
            {
                // Windows allows 10 fingers at most; ignore an 11th
                if (active.Count >= MaxContacts && !active.ContainsKey(id)) return true;

                bool alreadyDown = active.ContainsKey(id);
                active[id] = where;
                changedFlags = alreadyDown
                    ? (POINTER_FLAG_UPDATE | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT)
                    : (POINTER_FLAG_DOWN | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT);
            }
            else if (action == 'M')
            {
                if (!active.ContainsKey(id)) return true; // we never saw this finger go down
                active[id] = where;
                changedFlags = POINTER_FLAG_UPDATE | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT;
            }
            else if (action == 'U')
            {
                if (!active.ContainsKey(id)) return true;
                active[id] = where;
                changedFlags = POINTER_FLAG_UP;
            }
            else
            {
                return true; // some other letter: ignore it
            }

            bool ok = Inject(id, changedFlags);

            // A lifted finger is no longer down
            if (action == 'U') active.Remove(id);

            if (!ok) ready = false; // stop trying; the caller will switch to mouse mode
            return ok;
        }
    }

    // ==================================================================
    // ReleaseAll: lifts every finger that is still down. Used when the tablet
    // disconnects, so Windows is never left thinking a finger is stuck on the screen.
    // ==================================================================
    public void ReleaseAll()
    {
        lock (sync)
        {
            // A failed update disables future input, but previously injected fingers
            // still need a best-effort release before falling back to the mouse.
            if (active.Count > 0)
            {
                Inject(-1, POINTER_FLAG_UP); // -1 = "every finger gets the 'lifted' flag"
            }
            active.Clear();
        }
    }

    // ==================================================================
    // Inject: builds the list of all fingers and gives it to Windows.
    // The finger 'changedId' gets 'changedFlags'. Every other finger just "stays".
    // (If changedId is -1, EVERY finger gets changedFlags.)
    // ==================================================================
    bool Inject(int changedId, uint changedFlags)
    {
        List<POINTER_TOUCH_INFO> contacts = new List<POINTER_TOUCH_INFO>();

        foreach (KeyValuePair<int, POINT> finger in active)
        {
            uint flags;
            if (changedId == -1 || finger.Key == changedId)
            {
                flags = changedFlags;
            }
            else
            {
                flags = POINTER_FLAG_UPDATE | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT;
            }

            contacts.Add(MakeContact(finger.Key, finger.Value, flags));
        }

        if (contacts.Count == 0) return true;

        bool ok = InjectTouchInput((uint)contacts.Count, contacts.ToArray());
        if (!ok) LastError = Marshal.GetLastWin32Error();
        return ok;
    }

    // ==================================================================
    // MakeContact: fills in Windows' structure for one finger
    // ==================================================================
    static POINTER_TOUCH_INFO MakeContact(int id, POINT where, uint flags)
    {
        POINTER_TOUCH_INFO contact = new POINTER_TOUCH_INFO();

        contact.pointerInfo.pointerType = PT_TOUCH;
        contact.pointerInfo.pointerId = (uint)id;
        contact.pointerInfo.pointerFlags = flags;
        contact.pointerInfo.ptPixelLocation = where;

        // Pretend the fingertip is a small square, about 4 pixels wide, pressed medium-hard
        contact.touchFlags = 0;
        contact.touchMask = TOUCH_MASK_CONTACTAREA | TOUCH_MASK_ORIENTATION | TOUCH_MASK_PRESSURE;
        contact.rcContact.Left = where.X - 2;
        contact.rcContact.Top = where.Y - 2;
        contact.rcContact.Right = where.X + 2;
        contact.rcContact.Bottom = where.Y + 2;
        contact.orientation = 90;
        contact.pressure = 32000;

        return contact;
    }
}
