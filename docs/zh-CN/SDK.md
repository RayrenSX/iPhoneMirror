# Python SDK 与 stdio MCP

[文档索引](../README.md) · [English SDK guide](../../sdk/python/README.md)

需要 Python 3.10 或更新版本。在 iPhoneMirror 中启用本地自动化 API，把地址和密钥传给 SDK 或 MCP 客户端。 [本地自动化 API](AUTOMATION.md)

```powershell
python -m pip install -e ./sdk/python
# 同时使用 MCP 时
python -m pip install -e './sdk/python[mcp]'
$env:IPHONEMIRROR_API_URL = 'http://127.0.0.1:17890'
$env:IPHONEMIRROR_API_KEY = '<从开发者工具复制>'
```

```python
from iphonemirror import Client

client = Client(timeout=420)
print(client.devices())
phone = client.device("从设备列表复制目标 ID")
phone.prepare()
phone.open_window()
image = phone.screenshot()
with open("phone.png", "wb") as output:
    output.write(image.png)
```

有线或无线触控需要设备信任、绑定、开发者模式和匹配的 DDI。输入前通过 `with phone:` 获取独占租约并在退出时自动释放。只截图可直接调用 `screenshot()`。操作超时后先查状态，避免重复执行。

在 MCP 客户端以 stdio 启动 `iphonemirror-mcp` 或 `python -m iphonemirror.mcp_server`，并传入上面的环境变量。设备枚举、控制所有权、截图、输入和设备管理仍受桌面应用的权限及租约约束。
