#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <windows.graphics.capture.interop.h>
#include <windows.graphics.directx.direct3d11.interop.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Graphics.DirectX.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>
#include <memory>

using namespace winrt;
using namespace winrt::Windows::Graphics::Capture;
using namespace winrt::Windows::Graphics::DirectX;
using namespace winrt::Windows::Graphics::DirectX::Direct3D11;

struct Capture
{
    com_ptr<ID3D11Device> device;
    com_ptr<ID3D11DeviceContext> context;
    com_ptr<ID3D11Texture2D> staging;
    com_ptr<ID3D11Texture2D> latest;
    GraphicsCaptureItem item{ nullptr };
    Direct3D11CaptureFramePool pool{ nullptr };
    GraphicsCaptureSession session{ nullptr };
    bool mapped = false;
    UINT width = 0, height = 0;

    ~Capture()
    {
        if (mapped) context->Unmap(staging.get(), 0);
        try { if (session) session.Close(); } catch (...) {}
        try { if (pool) pool.Close(); } catch (...) {}
    }
};

extern "C" __declspec(dllexport) HRESULT __cdecl CaptureCreate(HMONITOR monitor, Capture** output)
{
    if (!output || !monitor) return E_INVALIDARG;
    *output = nullptr;
    try
    {
        if (!GraphicsCaptureSession::IsSupported()) return E_NOTIMPL;
        auto capture = std::make_unique<Capture>();
        check_hresult(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT, nullptr, 0, D3D11_SDK_VERSION,
            capture->device.put(), nullptr, capture->context.put()));
        auto dxgi = capture->device.as<IDXGIDevice>();
        com_ptr<IInspectable> inspectable;
        check_hresult(CreateDirect3D11DeviceFromDXGIDevice(dxgi.get(), inspectable.put()));
        auto device = inspectable.as<IDirect3DDevice>();
        auto factory = get_activation_factory<GraphicsCaptureItem, IGraphicsCaptureItemInterop>();
        check_hresult(factory->CreateForMonitor(monitor, guid_of<GraphicsCaptureItem>(), put_abi(capture->item)));
        capture->pool = Direct3D11CaptureFramePool::CreateFreeThreaded(device,
            DirectXPixelFormat::B8G8R8A8UIntNormalized, 2, capture->item.Size());
        capture->session = capture->pool.CreateCaptureSession(capture->item);
        capture->session.IsCursorCaptureEnabled(true);
        capture->session.StartCapture();
        *output = capture.release();
        return S_OK;
    }
    catch (...) { return to_hresult(); }
}

// S_FALSE means no new frame. Pointer stays valid until the next read or destroy.
extern "C" __declspec(dllexport) HRESULT __cdecl CaptureRead(Capture* capture, void** pixels,
    int* width, int* height, int* stride)
{
    if (!capture || !pixels || !width || !height || !stride) return E_INVALIDARG;
    *pixels = nullptr;
    try
    {
        auto frame = capture->pool.TryGetNextFrame();
        if (!frame) return S_FALSE;
        // Prefer the latest frame over replaying an old queue.
        while (auto newer = capture->pool.TryGetNextFrame()) { frame.Close(); frame = newer; }
        if (capture->mapped) { capture->context->Unmap(capture->staging.get(), 0); capture->mapped = false; }
        auto size = frame.ContentSize();
        auto access = frame.Surface().as<::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess>();
        com_ptr<ID3D11Texture2D> texture;
        check_hresult(access->GetInterface(guid_of<ID3D11Texture2D>(), texture.put_void()));
        D3D11_TEXTURE2D_DESC description;
        texture->GetDesc(&description);
        if (size.Width != static_cast<int>(description.Width) || size.Height != static_cast<int>(description.Height))
            return DXGI_ERROR_ACCESS_LOST; // Managed caller recreates for the new monitor size.
        if (!capture->staging || capture->width != description.Width || capture->height != description.Height)
        {
            capture->staging = nullptr;
            description.Usage = D3D11_USAGE_STAGING;
            description.BindFlags = 0;
            description.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
            description.MiscFlags = 0;
            check_hresult(capture->device->CreateTexture2D(&description, nullptr, capture->staging.put()));
            capture->width = description.Width; capture->height = description.Height;
        }
        capture->context->CopyResource(capture->staging.get(), texture.get());
        D3D11_MAPPED_SUBRESOURCE mapped;
        check_hresult(capture->context->Map(capture->staging.get(), 0, D3D11_MAP_READ, 0, &mapped));
        capture->mapped = true;
        *pixels = mapped.pData; *stride = static_cast<int>(mapped.RowPitch);
        *width = size.Width; *height = size.Height;
        frame.Close();
        return S_OK;
    }
    catch (...) { return to_hresult(); }
}

extern "C" __declspec(dllexport) void __cdecl CaptureDestroy(Capture* capture) { delete capture; }

// Borrowed pointers: valid until the next read/destroy; consumed on the capture thread.
extern "C" __declspec(dllexport) HRESULT __cdecl CaptureReadTexture(Capture* capture,
    ID3D11Device** device, ID3D11Texture2D** texture, int* width, int* height)
{
    if (!capture || !device || !texture || !width || !height) return E_INVALIDARG;
    *texture = nullptr; *device = nullptr;
    try
    {
        auto frame = capture->pool.TryGetNextFrame();
        if (!frame) return S_FALSE;
        while (auto newer = capture->pool.TryGetNextFrame()) { frame.Close(); frame = newer; }
        auto size = frame.ContentSize();
        auto access = frame.Surface().as<::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess>();
        com_ptr<ID3D11Texture2D> source;
        check_hresult(access->GetInterface(guid_of<ID3D11Texture2D>(), source.put_void()));
        D3D11_TEXTURE2D_DESC desc{};
        source->GetDesc(&desc);
        if (size.Width != static_cast<int>(desc.Width) || size.Height != static_cast<int>(desc.Height)) return DXGI_ERROR_ACCESS_LOST;
        D3D11_TEXTURE2D_DESC existing{};
        if (capture->latest) capture->latest->GetDesc(&existing);
        if (!capture->latest || existing.Width != desc.Width || existing.Height != desc.Height)
        {
            capture->latest = nullptr;
            desc.Usage = D3D11_USAGE_DEFAULT;
            desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
            desc.CPUAccessFlags = desc.MiscFlags = 0;
            check_hresult(capture->device->CreateTexture2D(&desc, nullptr, capture->latest.put()));
        }
        capture->context->CopyResource(capture->latest.get(), source.get());
        *device = capture->device.get(); *texture = capture->latest.get();
        *width = size.Width; *height = size.Height;
        frame.Close();
        return S_OK;
    }
    catch (...) { return to_hresult(); }
}

