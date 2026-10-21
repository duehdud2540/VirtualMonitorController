param(
    [string]$LibraryPath = "$PSScriptRoot\..\bin\FrameArrivalFix\VirtualMonitor.Capture.dll",
    [ValidateRange(1,60)][int]$Seconds = 10,
    [switch]$Legacy,
    [switch]$Animate
)
$ErrorActionPreference = 'Stop'
# Read-only measurement. Does not change display settings or save captured pixels.
Add-Type -TypeDefinition @'
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
public static class CaptureMeasurement {
 [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] struct DisplayDevice {
  public int Size;
  [MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)] public string Name;
  [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)] public string Description;
  public uint Flags;
  [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)] public string Id;
  [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)] public string Key;
 }
 [StructLayout(LayoutKind.Sequential)] struct Point { public int X,Y; }
 [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left,Top,Right,Bottom; }
 [StructLayout(LayoutKind.Sequential)] struct Message { public IntPtr Window;public uint Id;public IntPtr WParam,LParam;public uint Time;public Point Position;public uint Private; }
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr CreateWindowEx(uint ex,string cls,string title,uint style,int x,int y,int w,int h,IntPtr parent,IntPtr menu,IntPtr instance,IntPtr param);
 [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr window);
 [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr window);
 [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr window,IntPtr dc);
 [DllImport("user32.dll")] static extern int FillRect(IntPtr dc,ref Rect rect,IntPtr brush);
 [DllImport("gdi32.dll")] static extern IntPtr GetStockObject(int index);
 [DllImport("gdi32.dll")] static extern bool GdiFlush();
 [DllImport("user32.dll")] static extern bool PeekMessage(out Message msg,IntPtr window,uint min,uint max,uint remove);
 [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref Message msg);
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr LoadLibraryEx(string path,IntPtr file,uint flags);
 [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle,uint timeout);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern bool EnumDisplayDevices(string name,uint index,ref DisplayDevice d,uint flags);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern bool EnumDisplaySettingsEx(string name,int index,IntPtr mode,uint flags);
 [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(Point point,uint flags);
 [DllImport("combase.dll")] static extern int RoInitialize(uint type);
 [DllImport("combase.dll")] static extern void RoUninitialize();
 [DllImport("VirtualMonitor.Capture.dll",CallingConvention=CallingConvention.Cdecl)] static extern int CaptureCreate(IntPtr monitor,out IntPtr capture);
 [DllImport("VirtualMonitor.Capture.dll",CallingConvention=CallingConvention.Cdecl)] static extern void CaptureDestroy(IntPtr capture);
 [DllImport("VirtualMonitor.Capture.dll",CallingConvention=CallingConvention.Cdecl)] static extern int CaptureGetFrameEvent(IntPtr capture,out IntPtr ready);
 [DllImport("VirtualMonitor.Capture.dll",CallingConvention=CallingConvention.Cdecl)] static extern int CaptureReadTexture(IntPtr capture,out IntPtr device,out IntPtr texture,out int width,out int height);
 [DllImport("VirtualMonitor.Capture.dll",CallingConvention=CallingConvention.Cdecl)] static extern int CaptureGetStatistics(IntPtr capture,out long arrivals,out long reads,out long skipped,out long original,out long effective);
 public static void Run(string path,int seconds,bool legacy,bool animate) {
  if(LoadLibraryEx(path,IntPtr.Zero,0x1100)==IntPtr.Zero)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
  int apartment=RoInitialize(1); if(apartment<0 && apartment!=unchecked((int)0x80010106))Marshal.ThrowExceptionForHR(apartment);
  try {
   for(uint i=0;;i++) {
    var d=new DisplayDevice();d.Size=Marshal.SizeOf(d);
    if(!EnumDisplayDevices(null,i,ref d,0))break;
    if((d.Flags&1)==0 || d.Description.IndexOf("Virtual",StringComparison.OrdinalIgnoreCase)<0)continue;
    IntPtr dm=Marshal.AllocHGlobal(220),capture=IntPtr.Zero,window=IntPtr.Zero;
    try {
     Marshal.Copy(new byte[220],0,dm,220);Marshal.WriteInt16(dm,68,220);
     if(!EnumDisplaySettingsEx(d.Name,-1,dm,0))throw new Exception("Cannot read current mode");
     int w=Marshal.ReadInt32(dm,172),h=Marshal.ReadInt32(dm,176),hz=Marshal.ReadInt32(dm,184);
     var point=new Point{X=Marshal.ReadInt32(dm,76)+w/2,Y=Marshal.ReadInt32(dm,80)+h/2};
     if(animate){window=CreateWindowEx(0x08000080,"static","Capture rate test",0x90000000,point.X-160,point.Y-100,320,200,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);if(window==IntPtr.Zero)throw new Exception("Cannot create animation window");}
     Console.WriteLine("{0} {1}x{2} @ {3}Hz, legacy={4}",d.Name,w,h,hz,legacy);
     Marshal.ThrowExceptionForHR(CaptureCreate(MonitorFromPoint(point,0),out capture));
     IntPtr ready=IntPtr.Zero;if(!legacy)Marshal.ThrowExceptionForHR(CaptureGetFrameEvent(capture,out ready));
     long frames=0;var clock=Stopwatch.StartNew();double next=0,nextPaint=0;int paints=0;
     while(clock.Elapsed.TotalSeconds<seconds) {
      if(animate){Message msg;while(PeekMessage(out msg,IntPtr.Zero,0,0,1))DispatchMessage(ref msg);if(clock.Elapsed.TotalMilliseconds>=nextPaint){var rect=new Rect{Right=320,Bottom=200};IntPtr dc=GetDC(window);try{FillRect(dc,ref rect,GetStockObject((paints++%2)==0 ? 0:4));GdiFlush();}finally{ReleaseDC(window,dc);}nextPaint=clock.Elapsed.TotalMilliseconds+2;}}
      if(legacy) { if(clock.Elapsed.TotalMilliseconds<next){Thread.Sleep(1);continue;} next=Math.Max(next+1000.0/144,clock.Elapsed.TotalMilliseconds); }
      else {uint wait=WaitForSingleObject(ready,animate ? 1u:100u);if(wait==258)continue;if(wait!=0)throw new Exception("Frame wait failed: "+wait);}
      IntPtr device,texture;int width,height;
      int result=CaptureReadTexture(capture,out device,out texture,out width,out height);Marshal.ThrowExceptionForHR(result);if(result==0)frames++;
     }
     Console.WriteLine("Read FPS={0:F2}, frames={1}, seconds={2:F3}",frames/clock.Elapsed.TotalSeconds,frames,clock.Elapsed.TotalSeconds);
     if(!legacy){long arrivals,reads,skipped,original,effective;Marshal.ThrowExceptionForHR(CaptureGetStatistics(capture,out arrivals,out reads,out skipped,out original,out effective));Console.WriteLine("Arrivals={0}, reads={1}, skipped={2}, original interval={3:F4}ms, effective interval={4:F4}ms (-0.0001=unsupported)",arrivals,reads,skipped,original/10000.0,effective/10000.0);}
    } finally {if(capture!=IntPtr.Zero)CaptureDestroy(capture);if(window!=IntPtr.Zero)DestroyWindow(window);Marshal.FreeHGlobal(dm);}
   }
  } finally {if(apartment>=0)RoUninitialize();}
 }
}
'@
[CaptureMeasurement]::Run((Resolve-Path -LiteralPath $LibraryPath).Path, $Seconds, $Legacy.IsPresent, $Animate.IsPresent)
