# iPhone / iPad 面板分辨率目录

采集日期：2026-11-13。覆盖当日型号索引中的全部 **174 个 ProductType**，对应 **101 份 Apple 官方产品规格**。只包含 iPhone、iPad。

这里的尺寸是正常竖屏的物理面板像素，不是逻辑点数，也不是 USB/AirPlay 编码器必然输出的尺寸。收录旧设备的规格不代表新增了这些设备的投屏协议支持。只有实际接收并解码的帧才能证明输出达到了原生像素。

型号标识由 [DeviceKit](https://github.com/devicekit/DeviceKit) 提供，并与 [IPSW.me 型号索引](https://api.ipsw.me/v4/devices) 逐项对照；显示像素逐项来自下表的 Apple 官方规格页。iPad Pro M5 两种尺寸使用不同的官方规格页，修正了上游 13 英寸指向 11 英寸页面的问题。

新增型号若尚未进入 DeviceKit，则由更新脚本明确补录并与 IPSW 索引核对。iPhone Duo 的单项规格采用合上时的外屏，型号识别不表示已支持折叠内外屏自动切换。

统一数据位于 [apple-display-catalog.json](../config/apple-display-catalog.json)，其中保留每份规格页的标题、显示参数原文片段和 SHA256。完整下载的原始规格页保存在更新脚本的本地缓存，不下载固件、图片或整个网站。应用运行时使用随包内置数据，不联网查表。

## 横竖屏与实际输出

- 无线：设置中的原画档按面板分辨率分组，接收能力始终是“长边×长边”，横竖屏共用同一档。保留已有档位及默认 1080p；其他型号可选对应的新原画尺寸。首次发现发送端时尚未得到其型号，不会声称在发现之前已自动识别并替它修改协商。
- 有线 B：按完整 ProductType 匹配面板；iPad 原生用途保留已实测的对称能力范围，自定义宽高同样取长边构造对称请求，正常旋转无需重启 USB。
- 无线展示宽高以新解码帧为准，并随图像一同替换；普通有线 B 使用视频附件自动转正，原生 iPad B 另用只读系统方向。屏幕旋转不会把型号表尺寸强行覆盖到实际视频帧上。
- 日志分别记录型号、面板尺寸和解码尺寸，未知型号保持兼容回退，不把回退值称为真实原生尺寸。

## 产品规格

| 产品 | 型号标识 | 物理像素（宽×高） | 长边 | 官方来源 |
| --- | --- | --- | --- | --- |
| iPad | iPad1,1 | 768×1024 | 1024 | [Apple](https://support.apple.com/kb/SP580) |
| iPad mini (5th generation) | iPad11,1, iPad11,2 | 1536×2048 | 2048 | [Apple](https://support.apple.com/kb/SP788) |
| iPad Air (3rd generation) | iPad11,3, iPad11,4 | 1668×2224 | 2224 | [Apple](https://support.apple.com/kb/SP787) |
| iPad (8th generation) | iPad11,6, iPad11,7 | 1620×2160 | 2160 | [Apple](https://support.apple.com/kb/SP822) |
| iPad (9th generation) | iPad12,1, iPad12,2 | 1620×2160 | 2160 | [Apple](https://support.apple.com/kb/SP849) |
| iPad Air (4th generation) | iPad13,1, iPad13,2 | 1640×2360 | 2360 | [Apple](https://support.apple.com/kb/SP828) |
| iPad Air (5th generation) | iPad13,16, iPad13,17 | 1640×2360 | 2360 | [Apple](https://support.apple.com/kb/SP866) |
| iPad (10th generation) | iPad13,18, iPad13,19 | 1640×2360 | 2360 | [Apple](https://support.apple.com/kb/SP884) |
| iPad Pro (11-inch) (3rd generation) | iPad13,4, iPad13,5, iPad13,6, iPad13,7 | 1668×2388 | 2388 | [Apple](https://support.apple.com/kb/SP843) |
| iPad Pro (12.9-inch) (5th generation) | iPad13,8, iPad13,9, iPad13,10, iPad13,11 | 2048×2732 | 2732 | [Apple](https://support.apple.com/kb/SP844) |
| iPad mini (6th generation) | iPad14,1, iPad14,2 | 1488×2266 | 2266 | [Apple](https://support.apple.com/kb/SP850) |
| iPad Air (13-inch) (M2) | iPad14,10, iPad14,11 | 2048×2732 | 2732 | [Apple](https://support.apple.com/119893) |
| iPad Pro (11-inch) (4th generation) | iPad14,3, iPad14,4 | 1668×2388 | 2388 | [Apple](https://support.apple.com/kb/SP882) |
| iPad Pro (12.9-inch) (6th generation) | iPad14,5, iPad14,6 | 2048×2732 | 2732 | [Apple](https://support.apple.com/kb/SP883) |
| iPad Air (11-inch) (M2) | iPad14,8, iPad14,9 | 1640×2360 | 2360 | [Apple](https://support.apple.com/119894) |
| iPad Air (11-inch) (M3) | iPad15,3, iPad15,4 | 1640×2360 | 2360 | [Apple](https://support.apple.com/122241) |
| iPad Air (13-inch) (M3) | iPad15,5, iPad15,6 | 2048×2732 | 2732 | [Apple](https://support.apple.com/122242) |
| iPad (A16) | iPad15,7, iPad15,8 | 1640×2360 | 2360 | [Apple](https://support.apple.com/122240) |
| iPad mini (A17 Pro) | iPad16,1, iPad16,2 | 1488×2266 | 2266 | [Apple](https://support.apple.com/en-us/121456) |
| iPad Air (13-inch) (M4) | iPad16,10, iPad16,11 | 2048×2732 | 2732 | [Apple](https://support.apple.com/126472) |
| iPad Pro (11-inch) (M4) | iPad16,3, iPad16,4 | 1668×2420 | 2420 | [Apple](https://support.apple.com/en-us/119892) |
| iPad Pro (13-inch) (M4) | iPad16,5, iPad16,6 | 2064×2752 | 2752 | [Apple](https://support.apple.com/en-us/119891) |
| iPad Air (11-inch) (M4) | iPad16,8, iPad16,9 | 1640×2360 | 2360 | [Apple](https://support.apple.com/126471) |
| iPad Pro (11-inch) (M5) | iPad17,1, iPad17,2 | 1668×2420 | 2420 | [Apple](https://support.apple.com/en-us/125406) |
| iPad Pro (13-inch) (M5) | iPad17,3, iPad17,4 | 2064×2752 | 2752 | [Apple](https://support.apple.com/en-us/125407) |
| iPad mini (A20 Pro) | iPad19,1, iPad19,2 | 1512×2316 | 2316 | [Apple](https://support.apple.com/en-us/149482) |
| iPad 2 | iPad2,1, iPad2,2, iPad2,3, iPad2,4 | 768×1024 | 1024 | [Apple](https://support.apple.com/kb/SP622) |
| iPad mini | iPad2,5, iPad2,6, iPad2,7 | 768×1024 | 1024 | [Apple](https://support.apple.com/kb/SP661) |
| iPad (3rd generation) | iPad3,1, iPad3,2, iPad3,3 | 1536×2048 | 2048 | [Apple](https://support.apple.com/kb/SP647) |
| iPad (4th generation) | iPad3,4, iPad3,5, iPad3,6 | 1536×2048 | 2048 | [Apple](https://support.apple.com/kb/SP662) |
| iPad Air | iPad4,1, iPad4,2, iPad4,3 | 1536×2048 | 2048 | [Apple](https://support.apple.com/kb/SP692) |
| iPad mini 2 | iPad4,4, iPad4,5, iPad4,6 | 1536×2048 | 2048 | [Apple](https://support.apple.com/kb/SP693) |
| iPad mini 3 | iPad4,7, iPad4,8, iPad4,9 | 1536×2048 | 2048 | [Apple](https://support.apple.com/kb/SP709) |
| iPad mini 4 | iPad5,1, iPad5,2 | 1536×2048 | 2048 | [Apple](https://support.apple.com/kb/SP725) |
| iPad Air 2 | iPad5,3, iPad5,4 | 1536×2048 | 2048 | [Apple](https://support.apple.com/kb/SP708) |
| iPad (5th generation) | iPad6,11, iPad6,12 | 1536×2048 | 2048 | [Apple](https://support.apple.com/kb/SP751) |
| iPad Pro (9.7-inch) | iPad6,3, iPad6,4 | 1536×2048 | 2048 | [Apple](https://support.apple.com/kb/SP739) |
| iPad Pro (12.9-inch) | iPad6,7, iPad6,8 | 2048×2732 | 2732 | [Apple](https://support.apple.com/kb/SP723) |
| iPad Pro (12.9-inch) (2nd generation) | iPad7,1, iPad7,2 | 2048×2732 | 2732 | [Apple](https://support.apple.com/kb/SP761) |
| iPad (7th generation) | iPad7,11, iPad7,12 | 1620×2160 | 2160 | [Apple](https://support.apple.com/kb/SP807) |
| iPad Pro (10.5-inch) | iPad7,3, iPad7,4 | 1668×2224 | 2224 | [Apple](https://support.apple.com/kb/SP762) |
| iPad (6th generation) | iPad7,5, iPad7,6 | 1536×2048 | 2048 | [Apple](https://support.apple.com/kb/SP774) |
| iPad Pro (11-inch) | iPad8,1, iPad8,2, iPad8,3, iPad8,4 | 1668×2388 | 2388 | [Apple](https://support.apple.com/kb/SP784) |
| iPad Pro (12.9-inch) (4th generation) | iPad8,11, iPad8,12 | 2048×2732 | 2732 | [Apple](https://support.apple.com/kb/SP815) |
| iPad Pro (12.9-inch) (3rd generation) | iPad8,5, iPad8,6, iPad8,7, iPad8,8 | 2048×2732 | 2732 | [Apple](https://support.apple.com/kb/SP785) |
| iPad Pro (11-inch) (2nd generation) | iPad8,9, iPad8,10 | 1668×2388 | 2388 | [Apple](https://support.apple.com/kb/SP814) |
| iPhone | iPhone1,1 | 320×480 | 480 | [Apple](https://support.apple.com/kb/SP2) |
| iPhone 3G | iPhone1,2 | 320×480 | 480 | [Apple](https://support.apple.com/kb/SP495) |
| iPhone 8 | iPhone10,1, iPhone10,4 | 750×1334 | 1334 | [Apple](https://support.apple.com/kb/SP767) |
| iPhone 8 Plus | iPhone10,2, iPhone10,5 | 1080×1920 | 1920 | [Apple](https://support.apple.com/kb/SP768) |
| iPhone X | iPhone10,3, iPhone10,6 | 1125×2436 | 2436 | [Apple](https://support.apple.com/kb/SP770) |
| iPhone Xs | iPhone11,2 | 1125×2436 | 2436 | [Apple](https://support.apple.com/kb/SP779) |
| iPhone Xs Max | iPhone11,4, iPhone11,6 | 1242×2688 | 2688 | [Apple](https://support.apple.com/kb/SP780) |
| iPhone Xʀ | iPhone11,8 | 828×1792 | 1792 | [Apple](https://support.apple.com/kb/SP781) |
| iPhone 11 | iPhone12,1 | 828×1792 | 1792 | [Apple](https://support.apple.com/kb/SP804) |
| iPhone 11 Pro | iPhone12,3 | 1125×2436 | 2436 | [Apple](https://support.apple.com/kb/SP805) |
| iPhone 11 Pro Max | iPhone12,5 | 1242×2688 | 2688 | [Apple](https://support.apple.com/kb/SP806) |
| iPhone SE (2nd generation) | iPhone12,8 | 750×1334 | 1334 | [Apple](https://support.apple.com/kb/SP820) |
| iPhone 12 mini | iPhone13,1 | 1080×2340 | 2340 | [Apple](https://support.apple.com/kb/SP829) |
| iPhone 12 | iPhone13,2 | 1170×2532 | 2532 | [Apple](https://support.apple.com/kb/SP830) |
| iPhone 12 Pro | iPhone13,3 | 1170×2532 | 2532 | [Apple](https://support.apple.com/kb/SP831) |
| iPhone 12 Pro Max | iPhone13,4 | 1284×2778 | 2778 | [Apple](https://support.apple.com/kb/SP832) |
| iPhone 13 Pro | iPhone14,2 | 1170×2532 | 2532 | [Apple](https://support.apple.com/kb/SP852) |
| iPhone 13 Pro Max | iPhone14,3 | 1284×2778 | 2778 | [Apple](https://support.apple.com/kb/SP848) |
| iPhone 13 mini | iPhone14,4 | 1080×2340 | 2340 | [Apple](https://support.apple.com/kb/SP847) |
| iPhone 13 | iPhone14,5 | 1170×2532 | 2532 | [Apple](https://support.apple.com/kb/SP851) |
| iPhone SE (3rd generation) | iPhone14,6 | 750×1334 | 1334 | [Apple](https://support.apple.com/kb/SP867) |
| iPhone 14 | iPhone14,7 | 1170×2532 | 2532 | [Apple](https://support.apple.com/kb/SP873) |
| iPhone 14 Plus | iPhone14,8 | 1284×2778 | 2778 | [Apple](https://support.apple.com/kb/SP874) |
| iPhone 14 Pro | iPhone15,2 | 1179×2556 | 2556 | [Apple](https://support.apple.com/kb/SP875) |
| iPhone 14 Pro Max | iPhone15,3 | 1290×2796 | 2796 | [Apple](https://support.apple.com/kb/SP876) |
| iPhone 15 | iPhone15,4 | 1179×2556 | 2556 | [Apple](https://support.apple.com/kb/SP901) |
| iPhone 15 Plus | iPhone15,5 | 1290×2796 | 2796 | [Apple](https://support.apple.com/kb/SP902) |
| iPhone 15 Pro | iPhone16,1 | 1179×2556 | 2556 | [Apple](https://support.apple.com/kb/SP903) |
| iPhone 15 Pro Max | iPhone16,2 | 1290×2796 | 2796 | [Apple](https://support.apple.com/kb/SP904) |
| iPhone 16 Pro | iPhone17,1 | 1206×2622 | 2622 | [Apple](https://support.apple.com/121031) |
| iPhone 16 Pro Max | iPhone17,2 | 1320×2868 | 2868 | [Apple](https://support.apple.com/121032) |
| iPhone 16 | iPhone17,3 | 1179×2556 | 2556 | [Apple](https://support.apple.com/121029) |
| iPhone 16 Plus | iPhone17,4 | 1290×2796 | 2796 | [Apple](https://support.apple.com/121030) |
| iPhone 16e | iPhone17,5 | 1170×2532 | 2532 | [Apple](https://support.apple.com/122208) |
| iPhone 17 Pro | iPhone18,1 | 1206×2622 | 2622 | [Apple](https://support.apple.com/125090) |
| iPhone 17 Pro Max | iPhone18,2 | 1320×2868 | 2868 | [Apple](https://support.apple.com/125091) |
| iPhone 17 | iPhone18,3 | 1206×2622 | 2622 | [Apple](https://support.apple.com/125089) |
| iPhone Air | iPhone18,4 | 1260×2736 | 2736 | [Apple](https://support.apple.com/125092) |
| iPhone 17e | iPhone18,5 | 1170×2532 | 2532 | [Apple](https://support.apple.com/126470) |
| iPhone 18 Pro | iPhone19,2 | 1206×2622 | 2622 | [Apple](https://support.apple.com/en-us/148590) |
| iPhone 18 Pro Max | iPhone19,3, iPhone19,7 | 1320×2868 | 2868 | [Apple](https://support.apple.com/en-us/148591) |
| iPhone Duo | iPhone19,4 | 1398×2034 | 2034 | [Apple](https://support.apple.com/en-us/148588) |
| iPhone 3GS | iPhone2,1 | 320×480 | 480 | [Apple](https://support.apple.com/kb/SP565) |
| iPhone 4 | iPhone3,1, iPhone3,2, iPhone3,3 | 640×960 | 960 | [Apple](https://support.apple.com/kb/SP587) |
| iPhone 4s | iPhone4,1 | 640×960 | 960 | [Apple](https://support.apple.com/kb/SP643) |
| iPhone 5 | iPhone5,1, iPhone5,2 | 640×1136 | 1136 | [Apple](https://support.apple.com/kb/SP655) |
| iPhone 5c | iPhone5,3, iPhone5,4 | 640×1136 | 1136 | [Apple](https://support.apple.com/kb/SP684) |
| iPhone 5s | iPhone6,1, iPhone6,2 | 640×1136 | 1136 | [Apple](https://support.apple.com/kb/SP685) |
| iPhone 6 Plus | iPhone7,1 | 1080×1920 | 1920 | [Apple](https://support.apple.com/kb/SP706) |
| iPhone 6 | iPhone7,2 | 750×1334 | 1334 | [Apple](https://support.apple.com/kb/SP705) |
| iPhone 6s | iPhone8,1 | 750×1334 | 1334 | [Apple](https://support.apple.com/kb/SP726) |
| iPhone 6s Plus | iPhone8,2 | 1080×1920 | 1920 | [Apple](https://support.apple.com/kb/SP727) |
| iPhone SE | iPhone8,4 | 640×1136 | 1136 | [Apple](https://support.apple.com/kb/SP738) |
| iPhone 7 | iPhone9,1, iPhone9,3 | 750×1334 | 1334 | [Apple](https://support.apple.com/kb/SP743) |
| iPhone 7 Plus | iPhone9,2, iPhone9,4 | 1080×1920 | 1920 | [Apple](https://support.apple.com/kb/SP744) |

## 更新与校验

```powershell
python scripts/update_apple_display_catalog.py
python scripts/update_apple_display_catalog.py --check
```

更新脚本会拒绝型号重复、索引覆盖缺失、无官方显示像素或原生头文件与目录不一致的结果。需要重新下载上游最新数据时，指定新的 `--cache-dir`；使用已有缓存可加 `--offline` 复现。
