namespace Capture.Hook
{
    using System;
    using System.Collections.Generic;
    using System.IO.MemoryMappedFiles;
    using System.Runtime.InteropServices;
    using System.Threading;

    using Capture.Interface;

    using SharpDX;
    using SharpDX.Direct3D9;

    internal class DXHookD3D9Obs : BaseDXHook
    {
        #region Constants

        private const int NUM_BUFFERS = 3;

        private const int D3D9Ex_DEVICE_METHOD_COUNT = 15;

        private const int D3D9_DEVICE_METHOD_COUNT = 119;

        private const int SWAPCHAIN_METHOD_COUNT = 10;

        #endregion

        #region Fields

        private int curCPUTexture;
        private Thread hCopyThread;
        private ManualResetEvent hCopyEvent = new ManualResetEvent(false);
        private ManualResetEvent copyReadySignal = new ManualResetEvent(false);
        
        private bool bKillThread = false;
        private object[] dataMutexes = new object[NUM_BUFFERS] { new object(), new object(), new object() };
        
        private CopyData copyData = new CopyData();
        
        private readonly object endSceneLock = new object();

        private HookData Direct3DDeviceEx_PresentExHook = null;

        private HookData Direct3DDeviceEx_ResetExHook = null;

        private HookData Direct3DDevice_EndSceneHook = null;

        private HookData Direct3DDevice_PresentHook = null;

        private HookData Direct3DDevice_ResetHook = null;

        private HookData SwapChain_PresentHook = null;

        private Device currentDevice;

        private bool hasD3D9Ex;

        private List<IntPtr> id3dDeviceFunctionAddresses = new List<IntPtr>();

        private List<IntPtr> idSwapChainFunctionAddresses = new List<IntPtr>();

        private int presentHookRecurse = 0;

        private bool targetAcquired;

        private bool stopRequested;

        private bool isCapturing;

        private ManualResetEvent signalEnd = new ManualResetEvent(false);

        private bool bHasTextures;

        private bool[] issuedQueries = new bool[NUM_BUFFERS] { false, false, false };

        private Query[] queries = new Query[NUM_BUFFERS];

        private Surface[] textures = new Surface[NUM_BUFFERS];

        private IntPtr pCopyData;

        private bool[] lockedTextures = new bool[NUM_BUFFERS] { false, false, false };

        private long lastTime;

        private int curCapture;

        private Surface[] copyD3D9Textures = new Surface[NUM_BUFFERS];

        private int copyWait;
        private object[] textureMutexes = { new object(), new object() };

        private long keepAliveTime;

        private int resetCount;

        private bool hooksStarted;

        private MemoryMappedFile sharedMem;

        private int targetFps = 30;

        #endregion

        #region Constructors and Destructors

        public DXHookD3D9Obs(CaptureInterface ssInterface)
            : base(ssInterface)
        {
            InterfaceEventProxy.RecordingStarted += Interface_RecordingStarted;
            InterfaceEventProxy.RecordingStopped += Interface_RecordingStopped;
        }

        private void Interface_RecordingStopped()
        {
            stopRequested = true;
        }

        void Interface_RecordingStarted(CaptureConfig config)
        {
            this.targetFps = config.TargetFramesPerSecond;
            isCapturing = true;
        }

        #endregion

        #region Delegates

        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode, SetLastError = true)]
        private unsafe delegate int Direct3D9DeviceEx_PresentExDelegate(
            IntPtr devicePtr,
            SharpDX.Rectangle* pSourceRect,
            SharpDX.Rectangle* pDestRect,
            IntPtr hDestWindowOverride,
            IntPtr pDirtyRegion,
            Present dwFlags);

        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode, SetLastError = true)]
        private delegate int Direct3D9DeviceEx_ResetExDelegate(IntPtr devicePtr, ref PresentParameters presentParameters, DisplayModeEx displayModeEx);

        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode, SetLastError = true)]
        private delegate int Direct3D9Device_EndSceneDelegate(IntPtr device);

        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode, SetLastError = true)]
        private unsafe delegate int Direct3D9Device_PresentDelegate(
            IntPtr devicePtr,
            SharpDX.Rectangle* pSourceRect,
            SharpDX.Rectangle* pDestRect,
            IntPtr hDestWindowOverride,
            IntPtr pDirtyRegion);

        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode, SetLastError = true)]
        private delegate int Direct3D9Device_ResetDelegate(IntPtr device, ref PresentParameters presentParameters);

        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode, SetLastError = true)]
        private unsafe delegate int SwapChain_PresentDelegate(
            IntPtr devicePtr,
            SharpDX.Rectangle* pSourceRect,
            SharpDX.Rectangle* pDestRect,
            IntPtr hDestWindowOverride,
            IntPtr pDirtyRegion,
            Present dwFlags);

        #endregion

        #region Properties

        protected override string HookName
        {
            get
            {
                return "DXHookD3D9Ex";
            }
        }

        #endregion

        #region Public Methods and Operators

        public override void Cleanup()
        {
        }

        public override unsafe void Hook()
        {
            this.DebugMessage("Hook: Begin");

            this.DebugMessage("Hook: Before device creation");
            using (var d3d = new Direct3D())
            {
                this.DebugMessage("Hook: Direct3D created");
                using (
                    var device = new Device(
                        d3d,
                        0,
                        DeviceType.NullReference,
                        IntPtr.Zero,
                        CreateFlags.HardwareVertexProcessing,
                        new PresentParameters() { BackBufferWidth = 1, BackBufferHeight = 1 }))
                {
                    this.id3dDeviceFunctionAddresses.AddRange(this.GetVTblAddresses(device.NativePointer, D3D9_DEVICE_METHOD_COUNT));
                }
            }

            try
            {
                using (var d3dEx = new Direct3DEx())
                {
                    this.DebugMessage("Hook: Try Direct3DEx...");
                    using (
                        var deviceEx = new DeviceEx(
                            d3dEx,
                            0,
                            DeviceType.NullReference,
                            IntPtr.Zero,
                            CreateFlags.HardwareVertexProcessing,
                            new PresentParameters() { BackBufferWidth = 1, BackBufferHeight = 1 },
                            new DisplayModeEx() { Width = 800, Height = 600 }))
                    {
                        this.id3dDeviceFunctionAddresses.AddRange(
                            this.GetVTblAddresses(deviceEx.NativePointer, D3D9_DEVICE_METHOD_COUNT, D3D9Ex_DEVICE_METHOD_COUNT));
                        hasD3D9Ex = true;
                    }
                }
            }
            catch (Exception)
            {
                throw;
            }

            DebugMessage("Setting up Direct3D hooks...");
            this.Direct3DDevice_EndSceneHook = new HookData(
                this.id3dDeviceFunctionAddresses[(int)Direct3DDevice9FunctionOrdinals.EndScene],
                new Direct3D9Device_EndSceneDelegate(this.EndSceneHook),
                this);

            this.Direct3DDevice_EndSceneHook.ReHook();
            this.Hooks.Add(this.Direct3DDevice_EndSceneHook.Hook);

            this.Direct3DDevice_PresentHook = new HookData(
                this.id3dDeviceFunctionAddresses[(int)Direct3DDevice9FunctionOrdinals.Present],
                new Direct3D9Device_PresentDelegate(this.PresentHook),
                this);

            this.Direct3DDevice_ResetHook = new HookData(
                this.id3dDeviceFunctionAddresses[(int)Direct3DDevice9FunctionOrdinals.Reset],
                new Direct3D9Device_ResetDelegate(this.ResetHook),
                this);

            if (this.hasD3D9Ex)
            {
                DebugMessage("Setting up Direct3DEx hooks...");
                this.Direct3DDeviceEx_PresentExHook = new HookData(
                        this.id3dDeviceFunctionAddresses[(int)Direct3DDevice9ExFunctionOrdinals.PresentEx],
                        new Direct3D9DeviceEx_PresentExDelegate(this.PresentExHook),
                        this);

                this.Direct3DDeviceEx_ResetExHook = new HookData(
                        this.id3dDeviceFunctionAddresses[(int)Direct3DDevice9ExFunctionOrdinals.ResetEx],
                        new Direct3D9DeviceEx_ResetExDelegate(this.ResetExHook),
                        this);
            }

            this.Direct3DDevice_ResetHook.ReHook();
            this.Hooks.Add(this.Direct3DDevice_ResetHook.Hook);

            this.Direct3DDevice_PresentHook.ReHook();
            this.Hooks.Add(this.Direct3DDevice_PresentHook.Hook);

            if (hasD3D9Ex)
            {
                this.Direct3DDeviceEx_PresentExHook.ReHook();
                this.Hooks.Add(this.Direct3DDeviceEx_PresentExHook.Hook);

                this.Direct3DDeviceEx_ResetExHook.ReHook();
                this.Hooks.Add(this.Direct3DDeviceEx_ResetExHook.Hook);
            }

            this.DebugMessage("Hook: End");
        }

        #endregion

        #region Methods

        protected override void Dispose(bool disposing)
        {
            if (true)
            {
                this.Request = null;
                try
                {
                    ClearD3D9Data();
                    if (sharedMem != null)
                    {
                        sharedMem.Dispose();
                        sharedMem = null;
                    }
                }
                catch (Exception) { }
            }

            base.Dispose(disposing);
        }

        private void DoCaptureRenderTarget(Device device, string hook)
        {
            try
            {
                if (isCapturing && signalEnd.WaitOne(0))
                {
                    ClearD3D9Data();
                    isCapturing = false;
                    stopRequested = true;
                }

                if (!isCapturing)
                {
                    return;
                }

                if (!bHasTextures)
                {
                    using (Surface backbuffer = device.GetRenderTarget(0))
                    {
                        copyData.format = (int)backbuffer.Description.Format;
                        copyData.width = backbuffer.Description.Width;
                        copyData.height = backbuffer.Description.Height;
                    }
                    DoD3DHooks(device);
                }

                if (!bHasTextures)
                {
                    return;
                }
                
                long timeVal = DateTime.Now.Ticks;
                long frameTime = 1000000 / targetFps; // lock at 30 fps
                if (frameTime != 0)
                {
                    for (var i = 0; i < NUM_BUFFERS; i++)
                    {
                        if (issuedQueries[i])
                        {
                            bool tmp;
                            if (queries[i].GetData(out tmp, false))
                            {
                                issuedQueries[i] = false;

                                Surface targetTexture = textures[i];
                                try
                                {
                                    var lockedRect = targetTexture.LockRectangle(LockFlags.ReadOnly);
                                    pCopyData = lockedRect.DataPointer;

                                    curCPUTexture = i;
                                    lockedTextures[i] = true;
                                    hCopyEvent.Set();
                                }
                                catch (Exception ex)
                                {
                                    DebugMessage(ex.ToString());
                                }
                            }
                        }
                    }

                    long timeElapsed = timeVal - lastTime;
                    if (timeElapsed >= frameTime)
                    {
                        lastTime += frameTime;
                        if (timeElapsed > frameTime * 2)
                        {
                            lastTime = timeVal;
                        }

                        var nextCapture = (curCapture == NUM_BUFFERS - 1) ? 0 : (curCapture + 1);

                        Surface sourceTexture = copyD3D9Textures[curCapture];
                        using (var backbuffer = device.GetBackBuffer(0, 0))
                        {
                            device.StretchRectangle(backbuffer, sourceTexture, TextureFilter.None);
                        }
                        if (copyWait < (NUM_BUFFERS - 1))
                        {
                            copyWait++;
                        }
                        else
                        {
                            Surface prevSourceTexture = copyD3D9Textures[nextCapture];
                            Surface targetTexture = textures[nextCapture];

                            if (lockedTextures[nextCapture])
                            {
                                Monitor.Enter(dataMutexes[nextCapture]);

                                targetTexture.UnlockRectangle();
                                lockedTextures[nextCapture] = false;

                                Monitor.Exit(dataMutexes[nextCapture]);
                            }
                            try
                            {
                                device.GetRenderTargetData(prevSourceTexture, targetTexture);
                            }
                            catch (Exception ex)
                            {
                                DebugMessage(ex.ToString());
                            }

                            queries[nextCapture].Issue(Issue.End);
                            issuedQueries[nextCapture] = true;
                        }

                        curCapture = nextCapture;
                    }
                }
            }
            catch (Exception e)
            {
                DebugMessage("Error in PresentHook: " + e);
            }
        }

        private void DoD3DHooks(Device device)
        {
            bool bSuccess = true;

            for (int i = 0; i < NUM_BUFFERS; i++)
            {
                try
                {
                    textures[i] = Surface.CreateOffscreenPlain(device, copyData.width, copyData.height, (Format)copyData.format, Pool.SystemMemory);
                    if (i == (NUM_BUFFERS - 1))
                    {
                        var lr = textures[i].LockRectangle(LockFlags.ReadOnly);
                        copyData.pitch = lr.Pitch;
                        textures[i].UnlockRectangle();
                    }
                }
                catch (Exception ex)
                {
                    DebugMessage(ex.ToString());
                    bSuccess = false;
                    break;
                }
            }

            if (bSuccess)
            {
                for (var i = 0; i < NUM_BUFFERS; i++)
                {
                    try
                    {
                        copyD3D9Textures[i] = Surface.CreateRenderTarget(device, copyData.width, copyData.height, (Format)copyData.format, MultisampleType.None, 0, false);
                        queries[i] = new Query(device, QueryType.Event);
                    }
                    catch (Exception ex)
                    {
                        DebugMessage(ex.ToString());
                        bSuccess = false;
                    }
                }
            }

            if (bSuccess)
            {
                try
                {
                    bKillThread = false;
                    hCopyThread = new Thread(CopyD3D9CPUTextureThread);
                    hCopyThread.Start();
                }
                catch (Exception ex)
                {
                    DebugMessage(ex.ToString());
                }
            }

            if (bSuccess)
            {
                try
                {
                    sharedMem = MemoryMappedFile.CreateNew("CaptureHookSharedMem", copyData.pitch * copyData.height + (Marshal.SizeOf(typeof(CopyData))), MemoryMappedFileAccess.ReadWrite);
                }
                catch (Exception ex)
                {
                    DebugMessage(ex.ToString());
                    bSuccess = false;
                }
            }

            if (bSuccess)
            {
                bHasTextures = true;
                DebugMessage("Hooked Direct3D Surfaces.");
            }
            else
            {
                DebugMessage("Unknwown error during hooking Direct3D Surfaces.");
                ClearD3D9Data();
            }
        }

        private void CopyD3D9CPUTextureThread()
        {
            int sharedMemID = 0;
            while (hCopyEvent.WaitOne())
            {
                hCopyEvent.Reset();
                copyReadySignal.Reset();

                if (bKillThread)
                {
                    break;
                }
                
                int nextSharedMemID = sharedMemID == 0 ? 1 : 0;

                int copyTex = curCPUTexture;
                IntPtr data = pCopyData;
                if (copyTex < NUM_BUFFERS && data != IntPtr.Zero)
                {
                    try
                    {
                        Monitor.Enter(dataMutexes[copyTex]);

                        int lastRendered = -1;
                        bool locked = false;
                        try
                        {
                            locked = Monitor.TryEnter(textureMutexes[sharedMemID], 0);
                            if (locked)
                            {
                                lastRendered = sharedMemID;
                            }
                            else
                            {
                                locked = Monitor.TryEnter(textureMutexes[nextSharedMemID], 0);
                                if (locked)
                                {
                                    lastRendered = nextSharedMemID;
                                }
                            }

                            if (lastRendered != -1)
                            {
                                try
                                {
                                    var bpp = copyData.pitch / copyData.width;
                                    var size = copyData.height * copyData.pitch;
                                    var bdata = new byte[size];
                                    Marshal.Copy(data, bdata, 0, size);
                                    using (var stream = sharedMem.CreateViewAccessor())
                                    {
                                        this.copyData.lastRendered = lastRendered;
                                        // stream.Seek(0, SeekOrigin.Begin);
                                        stream.Write(0, ref copyData);
                                        stream.WriteArray(Marshal.SizeOf(typeof(CopyData)), bdata, 0, bdata.Length);
                                    }
                                    copyReadySignal.Set();
                                }
                                catch (Exception ex)
                                {
                                    DebugMessage(ex.ToString());
                                }
                            }
                        }
                        finally
                        {
                            if (locked)
                                Monitor.Exit(textureMutexes[lastRendered]);
                        }
                    }
                    finally
                    {
                        Monitor.Exit(dataMutexes[copyTex]);
                    }
                }

                sharedMemID = nextSharedMemID;
            }
        }

        private int EndSceneHook(IntPtr devicePtr)
        {
            var device = (Device)devicePtr;
            lock (this.endSceneLock)
            {
                if (!this.hooksStarted)
                {
                    this.hooksStarted = true;
                }

                if (currentDevice == null)
                {
                    if (!this.targetAcquired)
                    {
                        this.currentDevice = device;
                        this.SetupD3D9(device);
                        this.targetAcquired = true;
                    }
                }

                device.EndScene();
            }

            return Result.Ok.Code;
        }

        private unsafe int PresentExHook(
            IntPtr devicePtr,
            SharpDX.Rectangle* pSourceRect,
            SharpDX.Rectangle* pDestRect,
            IntPtr hDestWindowOverride,
            IntPtr pDirtyRegion,
            Present dwFlags)
        {
            try
            {
                var device = (DeviceEx)devicePtr;
                if (this.presentHookRecurse == 0 && hooksStarted)
                {
                    this.DoCaptureRenderTarget(device, "PresentEx");
                }

                this.presentHookRecurse++;
                var original = (Direct3D9DeviceEx_PresentExDelegate)Marshal.GetDelegateForFunctionPointer(this.id3dDeviceFunctionAddresses[(int)Direct3DDevice9ExFunctionOrdinals.PresentEx], typeof(Direct3D9DeviceEx_PresentExDelegate));
                original(devicePtr, pSourceRect, pDestRect, hDestWindowOverride, pDirtyRegion, dwFlags);
            }
            catch (Exception ex)
            {
                DebugMessage(ex.ToString());
            }
            finally
            {
                this.presentHookRecurse--;
            }
            return Result.Ok.Code;
        }

        private unsafe int PresentHook(
            IntPtr devicePtr,
            SharpDX.Rectangle* pSourceRect,
            SharpDX.Rectangle* pDestRect,
            IntPtr hDestWindowOverride,
            IntPtr pDirtyRegion)
        {
            try
            {
                Device device = (Device)devicePtr;
                if (presentHookRecurse == 0 && hooksStarted)
                {
                    this.DoCaptureRenderTarget(device, "PresentHook");
                }
                this.presentHookRecurse++;
                var original = (Direct3D9Device_PresentDelegate)Marshal.GetDelegateForFunctionPointer(this.id3dDeviceFunctionAddresses[(int)Direct3DDevice9FunctionOrdinals.Present], typeof(Direct3D9Device_PresentDelegate));
                original(devicePtr, pSourceRect, pDestRect, hDestWindowOverride, pDirtyRegion);
            }
            catch (Exception ex)
            {
                DebugMessage(ex.ToString());
            }
            finally
            {
                this.presentHookRecurse--;
            }
            return Result.Ok.Code;
        }

        private int ResetExHook(IntPtr deviceptr, ref PresentParameters presentparameters, DisplayModeEx displayModeEx)
        {
            try
            {
                DeviceEx device = (DeviceEx)deviceptr;
                if (hooksStarted)
                {
                    ClearD3D9Data();
                }

                device.ResetEx(ref presentparameters, displayModeEx);

                if (hooksStarted)
                {
                    if (currentDevice == null && !targetAcquired)
                    {
                        currentDevice = device;
                        targetAcquired = true;
                        hasD3D9Ex = true;
                    }

                    if (currentDevice == device)
                    {
                        SetupD3D9(device);
                    }
                }
            }
            catch (Exception ex)
            {
                DebugMessage(ex.ToString());
            }
            finally
            {
            }
            return Result.Ok.Code;
        }

        private void ClearD3D9Data()
        {
            DebugMessage("Clear D3D9 Data");
            bHasTextures = false;
            copyData.lastRendered = -1;
            copyData.pitch = 0;

            if (hCopyThread != null)
            {
                bKillThread = true;
                hCopyEvent.Set();

                if (hCopyThread.Join(500))
                    hCopyThread.Abort();

                hCopyEvent.Reset();
                copyReadySignal.Reset();
                hCopyThread = null;
            }
            
            for (int i = 0; i < NUM_BUFFERS; i++)
            {
                if (lockedTextures[i])
                {
                    Monitor.Enter(dataMutexes[i]);

                    textures[i].UnlockRectangle();
                    textures[i].Dispose();
                    lockedTextures[i] = false;

                    Monitor.Exit(dataMutexes[i]);
                }
                if (issuedQueries.Length >= i)
                {
                    issuedQueries[i] = false;
                }
                if (textures.Length >= i && textures[i] != null)
                {
                    textures[i].Dispose();
                    textures[i] = null;
                }
                if (copyD3D9Textures.Length >= i && copyD3D9Textures[i] != null)
                {
                    copyD3D9Textures[i].Dispose();
                    copyD3D9Textures[i] = null;
                }
                if (queries.Length >= i && queries[i] != null)
                {
                    queries[i].Dispose();
                    queries[i] = null;
                }
            }

            if (sharedMem != null)
            {
                sharedMem.Dispose();
                sharedMem = null;
            }
            //for (int i = 0; i < NUM_BUFFERS; i++)
            //{
            //    Monitor.Exit(dataMutexes[i]);
            //}

            copyWait = 0;
            lastTime = 0;
            curCapture = 0;
            curCPUTexture = 0;
            keepAliveTime = 0;
            resetCount++;
            // Marshal.FreeHGlobal(this.pCopyData);
        }

        private int ResetHook(IntPtr devicePtr, ref PresentParameters presentParameters)
        {
            try
            {
                lock (endSceneLock)
                {
                    Device device = (Device)devicePtr;
                    if (hooksStarted)
                    {
                        ClearD3D9Data();
                    }

                    device.Reset(presentParameters);

                    if (hooksStarted)
                    {
                        if (currentDevice == null && !targetAcquired)
                        {
                            currentDevice = device;
                            targetAcquired = true;
                        }

                        if (currentDevice == device)
                        {
                            SetupD3D9(device);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                DebugMessage(ex.ToString());
            }
            finally
            {
            }
            return Result.Ok.Code;
        }

        private void SetupD3D9(Device device)
        {
            try
            {
                DebugMessage("Setting up Direct3D...");
                using (SwapChain swapChain = device.GetSwapChain(0))
                {
                    PresentParameters pp = swapChain.PresentParameters;
                    this.copyData.width = pp.BackBufferWidth;
                    this.copyData.height = pp.BackBufferHeight;
                    this.copyData.format = (int)pp.BackBufferFormat;

                    DebugMessage(string.Format("D3D9 Setup: w: {0} h: {1} f: {2}", copyData.width, copyData.height, copyData.format));
                    isCapturing = true;
                    lastTime = 0;
                }
            }
            catch (Exception ex)
            {
                DebugMessage(ex.ToString());
            }
        }

        private unsafe int SwapChainPresentHook(
            IntPtr swapChainPtr,
            SharpDX.Rectangle* pSourceRect,
            SharpDX.Rectangle* pDestRect,
            IntPtr hDestWindowOverride,
            IntPtr pDirtyRegion,
            Present dwFlags)
        {
            SwapChain swapchain = (SwapChain)swapChainPtr;

            if (presentHookRecurse == 0 && hooksStarted)
            {
                DoCaptureRenderTarget(currentDevice, "SwapChainPresentHook");
            }

            presentHookRecurse++;
            var original = (SwapChain_PresentDelegate)Marshal.GetDelegateForFunctionPointer(this.id3dDeviceFunctionAddresses[(int)Direct3DSwapChain9Ordinals.Present], typeof(SwapChain_PresentDelegate));
            original(swapChainPtr, pSourceRect, pDestRect, hDestWindowOverride, pDirtyRegion, dwFlags);
            presentHookRecurse--;
            return Result.Ok.Code;
        }

        #endregion

    }
}