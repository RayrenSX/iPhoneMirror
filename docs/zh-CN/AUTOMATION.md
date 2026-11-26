# 本地自动化 API

[文档索引](../README.md) · [English API reference](../api/README.md)

本页以 v1.8.5-pre7 为基准。连续点击「关于」页的版本号五次打开「开发者工具」，在「自动化 API」中启用服务、选定端口并点击「应用」。默认端口为 `17890`，服务只监听 `127.0.0.1`。退出应用会停止服务。

## 访问接口

- 在浏览器打开 `http://127.0.0.1:17890/api/docs`，查看当前运行版本的接口说明。
- OpenAPI 地址为 `http://127.0.0.1:17890/api/v1/openapi.json`。
- 设备接口请求通过 `X-API-Key` 发送密钥。不要把密钥放入网址或公开日志。
- 用 `GET /api/v1/devices` 取得设备 ID。启用触控操作前，先完成设备信任、绑定和服务准备。

## 截图与反控

截图需要正在运行的投屏及解码帧。截图请求预算可在自动化设置中调为每秒 1 至 30 次。返回的几何信息描述实际截图与输入坐标。

输入前为明确选择的设备申请独占控制租约，后续请求携带 `X-Control-Session`。退出时释放租约，或在应用内点击「收回设备控制」。若方向、尺寸或会话改变，先重新读取状态和几何信息。输入超时后先检查手机上的实际结果。

```powershell
$api = 'http://127.0.0.1:17890/api/v1'
$headers = @{ 'X-API-Key' = $env:IPHONEMIRROR_API_KEY }
(Invoke-RestMethod "$api/devices" -Headers $headers).devices
```

## Python 与 MCP

Python SDK 和 stdio MCP 都使用上述本机 API。程序只连接在桌面应用中已配置的设备。已签名 IPA 安装等设备操作还需要对应设备权限和就绪的反控通道。 [Python SDK 和 MCP 中文说明](SDK.md)
