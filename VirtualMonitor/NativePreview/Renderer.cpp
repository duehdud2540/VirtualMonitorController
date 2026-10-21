#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d11.h>
#include <dxgi.h>
#include <d3dcompiler.h>
#include <wrl/client.h>
#include <memory>
#include <algorithm>
using Microsoft::WRL::ComPtr;
struct Renderer {
    HWND window;
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    ComPtr<IDXGISwapChain> swap;
    ComPtr<ID3D11RenderTargetView> target;
    ComPtr<ID3D11Texture2D> image;
    ComPtr<ID3D11ShaderResourceView> resource;
    ComPtr<ID3D11VertexShader> vertex;
    ComPtr<ID3D11PixelShader> pixel;
    ComPtr<ID3D11SamplerState> sampler;
    UINT width = 0, height = 0, imageWidth = 0, imageHeight = 0;
    bool diagnosticNoPresent = false;
};
#define CHECK(call) do { HRESULT hr = (call); if (FAILED(hr)) return hr; } while (0)
static HRESULT CreateRenderer(HWND window, ID3D11Device* sharedDevice, Renderer** output) {
    if (!window || !output) return E_INVALIDARG;
    *output = nullptr;
    auto r = std::make_unique<Renderer>();
    r->window = window;
    DXGI_SWAP_CHAIN_DESC desc{};
    desc.BufferDesc.Width = desc.BufferDesc.Height = 1;
    desc.BufferDesc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    desc.SampleDesc.Count = 1;
    desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    desc.BufferCount = 2;
    desc.OutputWindow = window;
    desc.Windowed = TRUE;
    desc.SwapEffect = DXGI_SWAP_EFFECT_DISCARD;
    if (sharedDevice) {
        r->device = sharedDevice;
        r->device->GetImmediateContext(&r->context);
        ComPtr<IDXGIDevice> dxgi;
        ComPtr<IDXGIAdapter> adapter;
        ComPtr<IDXGIFactory> factory;
        CHECK(r->device.As(&dxgi));
        CHECK(dxgi->GetAdapter(&adapter));
        CHECK(adapter->GetParent(IID_PPV_ARGS(&factory)));
        CHECK(factory->CreateSwapChain(r->device.Get(), &desc, &r->swap));
    } else {
        CHECK(D3D11CreateDeviceAndSwapChain(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT, nullptr, 0, D3D11_SDK_VERSION, &desc,
            &r->swap, &r->device, nullptr, &r->context));
    }
    ComPtr<IDXGIFactory> factory;
    CHECK(r->swap->GetParent(IID_PPV_ARGS(&factory)));
    CHECK(factory->MakeWindowAssociation(window, DXGI_MWA_NO_ALT_ENTER));
    const char source[] =
        "struct V { float4 p:SV_POSITION; float2 uv:TEXCOORD0; };"
        "V vs(uint id:SV_VertexID) { V o; float2 uv=float2((id<<1)&2,id&2);"
        "o.p=float4(uv*float2(2,-2)+float2(-1,1),0,1); o.uv=uv; return o; }"
        "Texture2D tex:register(t0); SamplerState smp:register(s0);"
        "float4 ps(V i):SV_TARGET { return float4(tex.Sample(smp,i.uv).rgb,1); }";
    ComPtr<ID3DBlob> vs, ps, error;
    CHECK(D3DCompile(source, sizeof(source)-1, nullptr, nullptr, nullptr, "vs", "vs_4_0", 0, 0, &vs, &error));
    error.Reset();
    CHECK(D3DCompile(source, sizeof(source)-1, nullptr, nullptr, nullptr, "ps", "ps_4_0", 0, 0, &ps, &error));
    CHECK(r->device->CreateVertexShader(vs->GetBufferPointer(), vs->GetBufferSize(), nullptr, &r->vertex));
    CHECK(r->device->CreatePixelShader(ps->GetBufferPointer(), ps->GetBufferSize(), nullptr, &r->pixel));
    D3D11_SAMPLER_DESC sampling{};
    sampling.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
    sampling.AddressU = sampling.AddressV = sampling.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
    sampling.MaxLOD = D3D11_FLOAT32_MAX;
    CHECK(r->device->CreateSamplerState(&sampling, &r->sampler));
    *output = r.release();
    return S_OK;
}
extern "C" __declspec(dllexport) HRESULT __cdecl PreviewCreate(HWND window, Renderer** output) {
    return CreateRenderer(window, nullptr, output);
}
extern "C" __declspec(dllexport) HRESULT __cdecl PreviewCreateOnDevice(HWND window, ID3D11Device* device, Renderer** output) {
    if (!device) return E_INVALIDARG;
    return CreateRenderer(window, device, output);
}
extern "C" __declspec(dllexport) HRESULT __cdecl PreviewDraw(Renderer* r, const void* pixels, int width, int height, int stride) {
    if (!r) return E_INVALIDARG;
    if (pixels) {
        if (width <= 0 || height <= 0 || width > 16384 || height > 16384 || stride < width * 4) return E_INVALIDARG;
        if (!r->image || r->imageWidth != (UINT)width || r->imageHeight != (UINT)height) {
            r->resource.Reset(); r->image.Reset();
            D3D11_TEXTURE2D_DESC desc{};
            desc.Width = width; desc.Height = height;
            desc.MipLevels = desc.ArraySize = 1;
            desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
            desc.SampleDesc.Count = 1;
            desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
            CHECK(r->device->CreateTexture2D(&desc, nullptr, &r->image));
            CHECK(r->device->CreateShaderResourceView(r->image.Get(), nullptr, &r->resource));
            r->imageWidth = width; r->imageHeight = height;
        }
        r->context->UpdateSubresource(r->image.Get(), 0, nullptr, pixels, stride, 0);
    }
    RECT rect{}; GetClientRect(r->window, &rect);
    UINT w = rect.right, h = rect.bottom;
    if (!w || !h || IsIconic(GetAncestor(r->window, GA_ROOT))) return S_FALSE;
    if (!r->target || r->width != w || r->height != h) {
        r->context->OMSetRenderTargets(0, nullptr, nullptr);
        r->target.Reset();
        CHECK(r->swap->ResizeBuffers(0, w, h, DXGI_FORMAT_UNKNOWN, 0));
        ComPtr<ID3D11Texture2D> back;
        CHECK(r->swap->GetBuffer(0, IID_PPV_ARGS(&back)));
        CHECK(r->device->CreateRenderTargetView(back.Get(), nullptr, &r->target));
        r->width = w; r->height = h;
    }
    const float black[4] = {0,0,0,1};
    r->context->ClearRenderTargetView(r->target.Get(), black);
    r->context->OMSetRenderTargets(1, r->target.GetAddressOf(), nullptr);
    if (r->resource) {
        float scale = (std::min)((float)w/r->imageWidth, (float)h/r->imageHeight);
        D3D11_VIEWPORT viewport{ (w-r->imageWidth*scale)/2, (h-r->imageHeight*scale)/2,
            r->imageWidth*scale, r->imageHeight*scale, 0, 1 };
        r->context->RSSetViewports(1, &viewport);
        r->context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        r->context->VSSetShader(r->vertex.Get(), nullptr, 0);
        r->context->PSSetShader(r->pixel.Get(), nullptr, 0);
        r->context->PSSetShaderResources(0, 1, r->resource.GetAddressOf());
        r->context->PSSetSamplers(0, 1, r->sampler.GetAddressOf());
        r->context->Draw(3, 0);
    }
    return r->diagnosticNoPresent ? S_OK : r->swap->Present(0, 0);
}
extern "C" __declspec(dllexport) void __cdecl PreviewClear(Renderer* r) {
    if (!r) return;
    r->context->ClearState();
    r->resource.Reset(); r->image.Reset();
    PreviewDraw(r, nullptr, 0, 0, 0);
}
extern "C" __declspec(dllexport) HRESULT __cdecl PreviewDrawTexture(Renderer* r, ID3D11Texture2D* texture) {
    if (!r || !texture) return E_INVALIDARG;
    ComPtr<ID3D11Device> owner;
    texture->GetDevice(&owner);
    if (owner.Get() != r->device.Get()) return E_INVALIDARG;
    if (r->image.Get() != texture) {
        r->resource.Reset();
        r->image = texture;
        D3D11_TEXTURE2D_DESC desc{};
        texture->GetDesc(&desc);
        r->imageWidth = desc.Width; r->imageHeight = desc.Height;
        CHECK(r->device->CreateShaderResourceView(texture, nullptr, &r->resource));
    }
    return PreviewDraw(r, nullptr, 0, 0, 0);
}
extern "C" __declspec(dllexport) void __cdecl PreviewDestroy(Renderer* r) { delete r; }

// Deterministic offscreen regression: WARP exercises the same shader/swap-chain path without a live display change.
extern "C" __declspec(dllexport) HRESULT __cdecl PreviewSelfTest() {
    HWND window = CreateWindowExW(0, L"static", L"VirtualMonitor renderer test", WS_POPUP,
        0, 0, 32, 32, nullptr, nullptr, nullptr, nullptr);
    if (!window) return HRESULT_FROM_WIN32(GetLastError());
    struct WindowGuard { HWND handle; ~WindowGuard() { DestroyWindow(handle); } } guard{window};
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    CHECK(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_WARP, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
        nullptr, 0, D3D11_SDK_VERSION, &device, nullptr, &context));
    Renderer* raw = nullptr;
    CHECK(CreateRenderer(window, device.Get(), &raw));
    std::unique_ptr<Renderer> renderer(raw);
    renderer->diagnosticNoPresent = true;
    const unsigned int pixels[4] = {0xff336699, 0xff336699, 0xff336699, 0xff336699};
    D3D11_TEXTURE2D_DESC desc{};
    desc.Width = desc.Height = 2;
    desc.MipLevels = desc.ArraySize = desc.SampleDesc.Count = 1;
    desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    D3D11_SUBRESOURCE_DATA data{pixels, 8, 0};
    ComPtr<ID3D11Texture2D> texture;
    CHECK(device->CreateTexture2D(&desc, &data, &texture));
    for (int pass = 0; pass < 2; pass++) {
        if (pass == 1 && !SetWindowPos(window, nullptr, 0, 0, 48, 24, SWP_NOACTIVATE | SWP_NOZORDER))
            return HRESULT_FROM_WIN32(GetLastError());
        CHECK(PreviewDrawTexture(renderer.get(), texture.Get()));
        ComPtr<ID3D11Texture2D> back, staging;
        CHECK(renderer->swap->GetBuffer(0, IID_PPV_ARGS(&back)));
        back->GetDesc(&desc);
        desc.Usage = D3D11_USAGE_STAGING;
        desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        desc.BindFlags = desc.MiscFlags = 0;
        CHECK(device->CreateTexture2D(&desc, nullptr, &staging));
        context->CopyResource(staging.Get(), back.Get());
        D3D11_MAPPED_SUBRESOURCE mapped{};
        CHECK(context->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &mapped));
        auto center = reinterpret_cast<const unsigned int*>(static_cast<const unsigned char*>(mapped.pData)
            + (desc.Height / 2) * mapped.RowPitch)[desc.Width / 2];
        context->Unmap(staging.Get(), 0);
        if (center != pixels[0]) return E_FAIL;
    }
    PreviewClear(renderer.get());
    return S_OK;
}
