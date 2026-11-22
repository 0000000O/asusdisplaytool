// AsusModeTool.cs — ASUS VG27AQML5A 极简安全 DDC/CI 控制工具 v2
//
// 安全设计（所有 VCP 寄存器与枚举均已从本机官方 DisplayWidgetCenter 1.5.3.4 及显示器实测核实）：
//   1. 仅匹配名称包含 "VG27AQML5A" 的物理显示器（使用 bool 标记，兼容 dxva2 句柄值 0）。
//   2. 写入接口严格白名单，仅允许 5 项已验证操作（无任意 VCP 写入入口）：
//        - 切换 sRGB 模式： VCP 0xDC = (当前高字节 & 0xFF00) | 0x03
//        - 切换 FPS 模式：  VCP 0xDC = (当前高字节 & 0xFF00) | 0x07
//        - 重置当前模式：   VCP 0xEC = 1（ASUS RestoreModeDefaults，绝不触碰 0x04 整机出厂重置）
//        - 可变刷新率 VRR： VCP 0xFD bit0（读改写保留其他位，与官方 VCPAPI.SetVRR 一致）
//        - 调节亮度：       VCP 0x10（严格限制在硬件回报的 0..Max 范围内）
//        - 调节显示器音量： VCP 0x62（严格限制在硬件回报的 0..Max 范围内）
//   3. 滑块拖动时仅本地预览（支持 20/40/60/80 磁吸，按住 Shift 可临时关闭吸附微调），松手后才发 1 次写指令，绝不轰炸 DDC/CI 总线。
//   4. 写前检测 DisplayWidgetCenter.exe 冲突；重置前二次确认；写入后自动回读显示器真实值并刷新 UI。
//   5. 零联网、零轮询、零托盘驻留，按 Esc 或关闭窗口即彻底退出。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AsusModeTool
{
    public enum TargetDisplayMode
    {
        Srgb = 0x03,
        Fps = 0x07
    }

    public sealed class MonitorSnapshot
    {
        public bool Success;
        public string MonitorName;
        public long MonitorHandleValue;

        public bool ModeOk;
        public uint ModeRaw;
        public uint ModeMax;
        public byte ModeHighByte;
        public byte ModeLowByte;
        public string ModeName;

        public bool BrightnessOk;
        public uint Brightness;
        public uint BrightnessMax;

        public bool VolumeOk;
        public uint Volume;
        public uint VolumeMax;

        public bool VrrOk;
        public bool VrrEnabled;
        public uint VrrRaw;
        public uint VrrMax;

        public long ElapsedMs;
        public string ErrorMessage;
        public List<string> FoundMonitors;
    }

    public sealed class MonitorWriteResult
    {
        public bool Success;
        public bool Skipped;
        public byte VcpCode;
        public uint WrittenValue;
        public long ElapsedMs;
        public string Message;
        public MonitorSnapshot Snapshot;
    }

    public static class DdcController
    {
        public const string TargetModelKeyword = "VG27AQML5A";

        // 严格白名单 VCP 寄存器
        private const byte VcpBrightness = 0x10;
        private const byte VcpAudioVolume = 0x62;
        private const byte VcpDisplayMode = 0xDC;
        private const byte VcpRestoreModeDefaults = 0xEC; // 非 0x04 全机恢复出厂
        private const byte VcpToggleSettings2 = 0xFD;     // bit0 (0x0001) = VRR 可变刷新率

        private const uint ResetCurrentModeValue = 1u;
        private const uint VrrBitMask = 0x0001u;

        private const int PreWriteDelayMs = 50;
        private const int ValueReadbackDelayMs = 100;
        private const int ModeReadbackDelayMs = 240;
        private const int ResetReadbackDelayMs = 300;
        private const int VrrInitialWaitMs = 900;         // VRR 切换可能触发 DP 短暂重握手
        private const int VrrRetryWaitMs = 450;
        private const int SliderCooldownMs = 250;
        private const int ModeCooldownMs = 600;

        private static readonly object IoLock = new object();
        private static long _lastWriteTickMs = 0;

        private static readonly Dictionary<byte, string> GamingModeNames = new Dictionary<byte, string>
        {
            { 0x01, "Cinema（影院）" },
            { 0x02, "Scenery（风景）" },
            { 0x03, "sRGB" },
            { 0x04, "User（用户）" },
            { 0x05, "Racing（比赛）" },
            { 0x06, "RTS/RPG" },
            { 0x07, "FPS" },
            { 0x08, "MOBA" },
            { 0x09, "NightVision（夜视）" },
            { 0x0A, "sRGB Calibration" }
        };

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct PHYSICAL_MONITOR
        {
            public IntPtr hPhysicalMonitor;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szPhysicalMonitorDescription;
        }

        private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, IntPtr lprcMonitor, IntPtr dwData);

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint pdwNumberOfPhysicalMonitors);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint dwPhysicalMonitorArraySize, [Out] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool DestroyPhysicalMonitors(uint dwPhysicalMonitorArraySize, PHYSICAL_MONITOR[] pPhysicalMonitorArray);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr hMonitor, byte bVCPCode, out int pvct, out uint pdwCurrentValue, out uint pdwMaximumValue);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool SetVCPFeature(IntPtr hMonitor, byte bVCPCode, uint dwNewValue);

        public static string FormatModeName(byte lowByte)
        {
            string name;
            if (GamingModeNames.TryGetValue(lowByte, out name))
            {
                return name;
            }
            return string.Format("未知模式 (0x{0:X2})", lowByte);
        }

        public static bool IsAsusWidgetRunning()
        {
            try
            {
                Process[] procs = Process.GetProcessesByName("DisplayWidgetCenter");
                bool running = procs != null && procs.Length > 0;
                if (procs != null)
                {
                    foreach (Process p in procs)
                    {
                        try { p.Dispose(); } catch { }
                    }
                }
                return running;
            }
            catch
            {
                return false;
            }
        }

        private static T WithTargetMonitor<T>(Func<IntPtr, string, List<string>, T> action, Func<List<string>, string, T> onNotFound)
        {
            List<string> foundNames = new List<string>();
            List<PHYSICAL_MONITOR[]> allocatedArrays = new List<PHYSICAL_MONITOR[]>();
            bool matched = false;
            IntPtr matchedHandle = IntPtr.Zero;
            string matchedName = null;

            try
            {
                EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate(IntPtr hMon, IntPtr hdc, IntPtr rect, IntPtr data)
                {
                    uint count = 0;
                    if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, out count) || count == 0)
                    {
                        return true;
                    }
                    PHYSICAL_MONITOR[] arr = new PHYSICAL_MONITOR[count];
                    if (!GetPhysicalMonitorsFromHMONITOR(hMon, count, arr))
                    {
                        return true;
                    }
                    allocatedArrays.Add(arr);
                    for (int i = 0; i < arr.Length; i++)
                    {
                        string desc = (arr[i].szPhysicalMonitorDescription ?? "").Trim();
                        foundNames.Add(desc);
                        // 重要：dxva2.dll 的首个物理显示器句柄可为 0 (IntPtr.Zero)，必须用 bool matched 判断
                        if (!matched &&
                            desc.IndexOf(TargetModelKeyword, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            matched = true;
                            matchedHandle = arr[i].hPhysicalMonitor;
                            matchedName = desc;
                        }
                    }
                    return true;
                }, IntPtr.Zero);

                if (!matched)
                {
                    string listText = foundNames.Count > 0 ? string.Join(", ", foundNames.ToArray()) : "无";
                    return onNotFound(foundNames, string.Format("未找到型号包含 {0} 的显示器（当前检测到：{1}）", TargetModelKeyword, listText));
                }

                return action(matchedHandle, matchedName, foundNames);
            }
            finally
            {
                foreach (PHYSICAL_MONITOR[] arr in allocatedArrays)
                {
                    try { DestroyPhysicalMonitors((uint)arr.Length, arr); } catch { }
                }
            }
        }

        private static MonitorSnapshot ReadSnapshotInternal(IntPtr hMon, string name, List<string> foundNames)
        {
            Stopwatch sw = Stopwatch.StartNew();
            MonitorSnapshot snap = new MonitorSnapshot();
            snap.MonitorName = name;
            snap.MonitorHandleValue = hMon.ToInt64();
            snap.FoundMonitors = foundNames;

            int vct;
            uint cur, max;

            // 1. 显示模式 (0xDC)
            snap.ModeOk = GetVCPFeatureAndVCPFeatureReply(hMon, VcpDisplayMode, out vct, out cur, out max);
            if (snap.ModeOk)
            {
                snap.ModeRaw = cur;
                snap.ModeMax = max;
                snap.ModeHighByte = (byte)((cur >> 8) & 0xFF);
                snap.ModeLowByte = (byte)(cur & 0xFF);
                snap.ModeName = FormatModeName(snap.ModeLowByte);
            }

            // 2. 亮度 (0x10)
            snap.BrightnessOk = GetVCPFeatureAndVCPFeatureReply(hMon, VcpBrightness, out vct, out cur, out max);
            if (snap.BrightnessOk)
            {
                snap.Brightness = cur;
                snap.BrightnessMax = max > 0 ? max : 100u;
            }

            // 3. 显示器音量 (0x62)
            snap.VolumeOk = GetVCPFeatureAndVCPFeatureReply(hMon, VcpAudioVolume, out vct, out cur, out max);
            if (snap.VolumeOk)
            {
                snap.Volume = cur;
                snap.VolumeMax = max > 0 ? max : 100u;
            }

            // 4. 可变刷新率 VRR (0xFD bit0)
            bool vrrRead = GetVCPFeatureAndVCPFeatureReply(hMon, VcpToggleSettings2, out vct, out cur, out max);
            if (vrrRead && max != 0 && max != 0xFEu)
            {
                snap.VrrOk = true;
                snap.VrrRaw = cur;
                snap.VrrMax = max;
                snap.VrrEnabled = (cur & VrrBitMask) != 0u;
            }

            sw.Stop();
            snap.ElapsedMs = sw.ElapsedMilliseconds;
            snap.Success = snap.ModeOk || snap.BrightnessOk || snap.VolumeOk || snap.VrrOk;
            if (!snap.Success)
            {
                snap.ErrorMessage = "读取 VCP 寄存器全部失败，请确认显示器 OSD 中 DDC/CI 已开启。";
            }
            return snap;
        }

        public static MonitorSnapshot ReadAll()
        {
            lock (IoLock)
            {
                return WithTargetMonitor<MonitorSnapshot>(
                    delegate(IntPtr hMon, string name, List<string> found)
                    {
                        return ReadSnapshotInternal(hMon, name, found);
                    },
                    delegate(List<string> found, string err)
                    {
                        return new MonitorSnapshot
                        {
                            Success = false,
                            FoundMonitors = found,
                            ErrorMessage = err
                        };
                    });
            }
        }

        private static string CheckCooldown(int requiredMs)
        {
            long now = Environment.TickCount & 0x7FFFFFFF;
            long prev = _lastWriteTickMs;
            if (prev > 0)
            {
                long diff = now - prev;
                if (diff >= 0 && diff < requiredMs)
                {
                    return string.Format("操作过快，请稍候 {0} ms 再试（防 DDC/CI 频写保护）。", requiredMs - diff);
                }
            }
            return null;
        }

        private static void MarkWriteTimestamp()
        {
            _lastWriteTickMs = Environment.TickCount & 0x7FFFFFFF;
        }

        public static MonitorWriteResult SwitchDisplayMode(TargetDisplayMode target)
        {
            lock (IoLock)
            {
                string cooldownErr = CheckCooldown(ModeCooldownMs);
                if (cooldownErr != null)
                {
                    return new MonitorWriteResult { Success = false, Message = cooldownErr };
                }

                byte targetLow = (byte)target;
                string targetLabel = FormatModeName(targetLow);

                return WithTargetMonitor<MonitorWriteResult>(
                    delegate(IntPtr hMon, string name, List<string> found)
                    {
                        Stopwatch sw = Stopwatch.StartNew();
                        int vct;
                        uint curMode, maxMode;
                        bool readOk = GetVCPFeatureAndVCPFeatureReply(hMon, VcpDisplayMode, out vct, out curMode, out maxMode);
                        if (!readOk)
                        {
                            int err = Marshal.GetLastWin32Error();
                            sw.Stop();
                            return new MonitorWriteResult
                            {
                                Success = false,
                                ElapsedMs = sw.ElapsedMilliseconds,
                                Message = string.Format("切换前读取 0xDC 失败（Win32={0}）。", err)
                            };
                        }

                        if ((byte)(curMode & 0xFF) == targetLow)
                        {
                            MonitorSnapshot snapSame = ReadSnapshotInternal(hMon, name, found);
                            sw.Stop();
                            return new MonitorWriteResult
                            {
                                Success = true,
                                Skipped = true,
                                VcpCode = VcpDisplayMode,
                                WrittenValue = curMode,
                                ElapsedMs = sw.ElapsedMilliseconds,
                                Snapshot = snapSame,
                                Message = string.Format("当前已是 {0} 模式（0x{1:X4}），跳过重复写入。", targetLabel, curMode)
                            };
                        }

                        // 按华硕官方规则保留高字节 ColorRichness，低字节写目标模式
                        uint targetValue = (curMode & 0xFF00u) | (uint)targetLow;
                        Thread.Sleep(PreWriteDelayMs);
                        bool ok = SetVCPFeature(hMon, VcpDisplayMode, targetValue);
                        int win32Err = ok ? 0 : Marshal.GetLastWin32Error();
                        MarkWriteTimestamp();

                        if (!ok)
                        {
                            sw.Stop();
                            return new MonitorWriteResult
                            {
                                Success = false,
                                VcpCode = VcpDisplayMode,
                                WrittenValue = targetValue,
                                ElapsedMs = sw.ElapsedMilliseconds,
                                Message = string.Format("切换 {0} 失败：写入 0xDC=0x{1:X4} 返回 Win32={2}。", targetLabel, targetValue, win32Err)
                            };
                        }

                        Thread.Sleep(ModeReadbackDelayMs);
                        MonitorSnapshot after = ReadSnapshotInternal(hMon, name, found);
                        sw.Stop();

                        string msg;
                        if (after.ModeOk && after.ModeLowByte == targetLow)
                        {
                            msg = string.Format("已切换为 {0}（0xDC=0x{1:X4}，回读亮度={2}，耗时 {3}ms）。",
                                targetLabel, after.ModeRaw, after.BrightnessOk ? after.Brightness.ToString() : "--", sw.ElapsedMilliseconds);
                        }
                        else if (after.ModeOk)
                        {
                            msg = string.Format("已发送 0xDC=0x{0:X4}，但回读为 {1} (0x{2:X4})，可能处于 HDR 锁定。",
                                targetValue, after.ModeName, after.ModeRaw);
                        }
                        else
                        {
                            msg = string.Format("已发送 0xDC=0x{0:X4}，但回读异常。", targetValue);
                        }

                        return new MonitorWriteResult
                        {
                            Success = true,
                            Skipped = false,
                            VcpCode = VcpDisplayMode,
                            WrittenValue = targetValue,
                            ElapsedMs = sw.ElapsedMilliseconds,
                            Snapshot = after,
                            Message = msg
                        };
                    },
                    delegate(List<string> found, string err)
                    {
                        return new MonitorWriteResult { Success = false, Message = err };
                    });
            }
        }

        public static MonitorWriteResult ResetCurrentMode()
        {
            lock (IoLock)
            {
                string cooldownErr = CheckCooldown(ModeCooldownMs);
                if (cooldownErr != null)
                {
                    return new MonitorWriteResult { Success = false, Message = cooldownErr };
                }

                return WithTargetMonitor<MonitorWriteResult>(
                    delegate(IntPtr hMon, string name, List<string> found)
                    {
                        Stopwatch sw = Stopwatch.StartNew();
                        Thread.Sleep(PreWriteDelayMs);
                        bool ok = SetVCPFeature(hMon, VcpRestoreModeDefaults, ResetCurrentModeValue);
                        int win32Err = ok ? 0 : Marshal.GetLastWin32Error();
                        MarkWriteTimestamp();

                        if (!ok)
                        {
                            sw.Stop();
                            return new MonitorWriteResult
                            {
                                Success = false,
                                VcpCode = VcpRestoreModeDefaults,
                                WrittenValue = ResetCurrentModeValue,
                                ElapsedMs = sw.ElapsedMilliseconds,
                                Message = string.Format("写入 0xEC=1 失败（Win32={0}）。", win32Err)
                            };
                        }

                        Thread.Sleep(ResetReadbackDelayMs);
                        MonitorSnapshot after = ReadSnapshotInternal(hMon, name, found);
                        sw.Stop();

                        string msg = after.Success
                            ? string.Format("已重置当前模式（0xEC=1，回读：模式={0}，亮度={1}，音量={2}，耗时 {3}ms）。",
                                after.ModeOk ? after.ModeName : "--",
                                after.BrightnessOk ? after.Brightness.ToString() : "--",
                                after.VolumeOk ? after.Volume.ToString() : "--",
                                sw.ElapsedMilliseconds)
                            : "已发送 0xEC=1，但回读状态异常。";

                        return new MonitorWriteResult
                        {
                            Success = true,
                            Skipped = false,
                            VcpCode = VcpRestoreModeDefaults,
                            WrittenValue = ResetCurrentModeValue,
                            ElapsedMs = sw.ElapsedMilliseconds,
                            Snapshot = after,
                            Message = msg
                        };
                    },
                    delegate(List<string> found, string err)
                    {
                        return new MonitorWriteResult { Success = false, Message = err };
                    });
            }
        }

        public static MonitorWriteResult SetBrightness(int requestedValue)
        {
            return SetScalarVcp(VcpBrightness, "亮度", requestedValue);
        }

        public static MonitorWriteResult SetVolume(int requestedValue)
        {
            return SetScalarVcp(VcpAudioVolume, "显示器音量", requestedValue);
        }

        public static MonitorWriteResult SetVrr(bool enable)
        {
            lock (IoLock)
            {
                string cooldownErr = CheckCooldown(ModeCooldownMs);
                if (cooldownErr != null)
                {
                    return new MonitorWriteResult { Success = false, Message = cooldownErr };
                }

                Stopwatch sw = Stopwatch.StartNew();
                bool needReadback = false;
                uint writtenVal = 0u;

                MonitorWriteResult step1 = WithTargetMonitor<MonitorWriteResult>(
                    delegate(IntPtr hMon, string name, List<string> found)
                    {
                        int vct;
                        uint cur, max;
                        bool readOk = GetVCPFeatureAndVCPFeatureReply(hMon, VcpToggleSettings2, out vct, out cur, out max);
                        if (!readOk || max == 0u || max == 0xFEu)
                        {
                            int err = readOk ? 0 : Marshal.GetLastWin32Error();
                            return new MonitorWriteResult
                            {
                                Success = false,
                                Message = string.Format("读取 VRR 状态 (0xFD) 失败或当前不可用（ok={0}, max=0x{1:X}, Win32={2}），已中止写入以保护其他位。", readOk, max, err)
                            };
                        }

                        bool currentOn = (cur & VrrBitMask) != 0u;
                        if (currentOn == enable)
                        {
                            MonitorSnapshot sameSnap = ReadSnapshotInternal(hMon, name, found);
                            return new MonitorWriteResult
                            {
                                Success = true,
                                Skipped = true,
                                VcpCode = VcpToggleSettings2,
                                WrittenValue = cur,
                                Snapshot = sameSnap,
                                Message = string.Format("可变刷新率 (VRR) 当前已为「{0}」（0xFD=0x{1:X4}），跳过重复写入。", enable ? "开启" : "关闭", cur)
                            };
                        }

                        // 与官方 VCPAPI.SetVRR -> SetToggleSettingsItem 完全一致：仅翻转 bit0，保留其余位
                        uint nextVal = enable ? (cur | VrrBitMask) : (cur & ~VrrBitMask);
                        Thread.Sleep(PreWriteDelayMs);
                        bool ok = SetVCPFeature(hMon, VcpToggleSettings2, nextVal);
                        int win32Err = ok ? 0 : Marshal.GetLastWin32Error();
                        MarkWriteTimestamp();

                        if (!ok)
                        {
                            return new MonitorWriteResult
                            {
                                Success = false,
                                VcpCode = VcpToggleSettings2,
                                WrittenValue = nextVal,
                                Message = string.Format("切换 VRR 失败：写入 0xFD=0x{0:X4} 返回 Win32={1}。", nextVal, win32Err)
                            };
                        }

                        needReadback = true;
                        writtenVal = nextVal;
                        return new MonitorWriteResult
                        {
                            Success = true,
                            Skipped = false,
                            VcpCode = VcpToggleSettings2,
                            WrittenValue = nextVal
                        };
                    },
                    delegate(List<string> found, string err)
                    {
                        return new MonitorWriteResult { Success = false, Message = err };
                    });

                if (!needReadback)
                {
                    sw.Stop();
                    step1.ElapsedMs = sw.ElapsedMilliseconds;
                    return step1;
                }

                // 释放旧显示器句柄后再等待并重新获取句柄回读，避免 VRR 触发 DP 链路重握手导致旧句柄失效
                Thread.Sleep(VrrInitialWaitMs);
                MonitorSnapshot after = null;
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    after = WithTargetMonitor<MonitorSnapshot>(
                        delegate(IntPtr hMon, string name, List<string> found)
                        {
                            return ReadSnapshotInternal(hMon, name, found);
                        },
                        delegate(List<string> found, string err)
                        {
                            return new MonitorSnapshot { Success = false, FoundMonitors = found, ErrorMessage = err };
                        });
                    if (after.VrrOk && after.VrrEnabled == enable) break;
                    if (attempt < 2) Thread.Sleep(VrrRetryWaitMs);
                }

                sw.Stop();
                string targetStr = enable ? "开启" : "关闭";
                string msg;
                if (after != null && after.VrrOk && after.VrrEnabled == enable)
                {
                    msg = string.Format("可变刷新率 (VRR) 已{0}（写入 0xFD=0x{1:X4}，回读=0x{2:X4}，耗时 {3}ms）。",
                        targetStr, writtenVal, after.VrrRaw, sw.ElapsedMilliseconds);
                }
                else if (after != null && after.VrrOk)
                {
                    msg = string.Format("已发送 VRR {0}（0xFD=0x{1:X4}），但回读仍为「{2}」(0x{3:X4})，可能被 ELMB 等模式互斥锁定。",
                        targetStr, writtenVal, after.VrrEnabled ? "开启" : "关闭", after.VrrRaw);
                }
                else
                {
                    msg = string.Format("已发送 VRR {0}（0xFD=0x{1:X4}），屏幕可能正在重握手，请稍后点「刷新全部」确认。", targetStr, writtenVal);
                }

                step1.ElapsedMs = sw.ElapsedMilliseconds;
                step1.Snapshot = after;
                step1.Message = msg;
                return step1;
            }
        }

        private static MonitorWriteResult SetScalarVcp(byte vcpCode, string label, int requestedValue)
        {
            // 双重白名单防线
            if (vcpCode != VcpBrightness && vcpCode != VcpAudioVolume)
            {
                return new MonitorWriteResult { Success = false, Message = "拒绝非白名单 VCP 写入。" };
            }

            lock (IoLock)
            {
                string cooldownErr = CheckCooldown(SliderCooldownMs);
                if (cooldownErr != null)
                {
                    return new MonitorWriteResult { Success = false, Message = cooldownErr };
                }

                return WithTargetMonitor<MonitorWriteResult>(
                    delegate(IntPtr hMon, string name, List<string> found)
                    {
                        Stopwatch sw = Stopwatch.StartNew();
                        int vct;
                        uint cur, max;
                        bool readOk = GetVCPFeatureAndVCPFeatureReply(hMon, vcpCode, out vct, out cur, out max);
                        uint safeMax = (readOk && max > 0) ? max : 100u;
                        uint clamped = (uint)Math.Max(0, Math.Min((int)safeMax, requestedValue));

                        if (readOk && cur == clamped)
                        {
                            MonitorSnapshot snapSame = ReadSnapshotInternal(hMon, name, found);
                            sw.Stop();
                            return new MonitorWriteResult
                            {
                                Success = true,
                                Skipped = true,
                                VcpCode = vcpCode,
                                WrittenValue = clamped,
                                ElapsedMs = sw.ElapsedMilliseconds,
                                Snapshot = snapSame,
                                Message = string.Format("{0}当前已为 {1}/{2}，跳过重复写入。", label, clamped, safeMax)
                            };
                        }

                        Thread.Sleep(PreWriteDelayMs);
                        bool ok = SetVCPFeature(hMon, vcpCode, clamped);
                        int win32Err = ok ? 0 : Marshal.GetLastWin32Error();
                        MarkWriteTimestamp();

                        if (!ok)
                        {
                            sw.Stop();
                            return new MonitorWriteResult
                            {
                                Success = false,
                                VcpCode = vcpCode,
                                WrittenValue = clamped,
                                ElapsedMs = sw.ElapsedMilliseconds,
                                Message = string.Format("设置{0}失败：写入 0x{1:X2}={2} 返回 Win32={3}。", label, vcpCode, clamped, win32Err)
                            };
                        }

                        Thread.Sleep(ValueReadbackDelayMs);
                        MonitorSnapshot after = ReadSnapshotInternal(hMon, name, found);
                        sw.Stop();

                        uint actual = (vcpCode == VcpBrightness) ? after.Brightness : after.Volume;
                        bool actualOk = (vcpCode == VcpBrightness) ? after.BrightnessOk : after.VolumeOk;
                        string msg = actualOk
                            ? string.Format("{0}已设为 {1}（写入 0x{2:X2}={3}，回读={1}/{4}，耗时 {5}ms）。", label, actual, vcpCode, clamped, safeMax, sw.ElapsedMilliseconds)
                            : string.Format("已发送{0} 0x{1:X2}={2}，但回读失败。", label, vcpCode, clamped);

                        return new MonitorWriteResult
                        {
                            Success = true,
                            Skipped = false,
                            VcpCode = vcpCode,
                            WrittenValue = clamped,
                            ElapsedMs = sw.ElapsedMilliseconds,
                            Snapshot = after,
                            Message = msg
                        };
                    },
                    delegate(List<string> found, string err)
                    {
                        return new MonitorWriteResult { Success = false, Message = err };
                    });
            }
        }
    }

    /// <summary>
    /// 带磁吸刻度的自绘滑动条：在 20/40/60/80 处带视觉刻度和磁吸吸附，松手时触发单次 ValueCommitted。
    /// </summary>
    public sealed class SnapSlider : Control
    {
        private int _minimum = 0;
        private int _maximum = 100;
        private int _value = 50;
        private int[] _snapPoints = new int[] { 20, 40, 60, 80 };
        private int _snapThreshold = 3;
        private bool _dragging = false;
        private bool _snapped = false;
        private Color _accentColor = Color.FromArgb(245, 158, 11);

        public event EventHandler ValuePreviewChanged;
        public event EventHandler ValueCommitted;

        public SnapSlider()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.Selectable, true);
            Cursor = Cursors.Hand;
            TabStop = true;
            Height = 42;
        }

        public int Minimum
        {
            get { return _minimum; }
            set { _minimum = value; Invalidate(); }
        }

        public int Maximum
        {
            get { return _maximum; }
            set { _maximum = Math.Max(_minimum + 1, value); if (_value > _maximum) _value = _maximum; Invalidate(); }
        }

        public int Value
        {
            get { return _value; }
            set
            {
                int v = Math.Max(_minimum, Math.Min(_maximum, value));
                if (v != _value)
                {
                    _value = v;
                    _snapped = IsExactSnapPoint(_value);
                    Invalidate();
                }
            }
        }

        public bool IsDragging { get { return _dragging; } }
        public bool IsSnapped { get { return _snapped; } }

        public int[] SnapPoints
        {
            get { return _snapPoints; }
            set { _snapPoints = value ?? new int[0]; Invalidate(); }
        }

        public int SnapThreshold
        {
            get { return _snapThreshold; }
            set { _snapThreshold = Math.Max(0, value); }
        }

        public Color AccentColor
        {
            get { return _accentColor; }
            set { _accentColor = value; Invalidate(); }
        }

        private bool IsExactSnapPoint(int val)
        {
            if (_snapPoints == null) return false;
            for (int i = 0; i < _snapPoints.Length; i++)
            {
                if (_snapPoints[i] == val) return true;
            }
            return false;
        }

        public int ApplySnap(int rawVal, bool bypassSnap, out bool snapped)
        {
            int clamped = Math.Max(_minimum, Math.Min(_maximum, rawVal));
            snapped = false;
            if (!bypassSnap && _snapPoints != null && _snapThreshold > 0)
            {
                int bestDist = int.MaxValue;
                int bestPoint = clamped;
                for (int i = 0; i < _snapPoints.Length; i++)
                {
                    int sp = _snapPoints[i];
                    if (sp < _minimum || sp > _maximum) continue;
                    int d = Math.Abs(clamped - sp);
                    if (d <= _snapThreshold && d < bestDist)
                    {
                        bestDist = d;
                        bestPoint = sp;
                        snapped = true;
                    }
                }
                if (snapped) return bestPoint;
            }
            return clamped;
        }

        private Rectangle GetTrackRect()
        {
            int padX = 14;
            int trackH = 6;
            int trackY = 12;
            return new Rectangle(padX, trackY, Math.Max(20, Width - padX * 2), trackH);
        }

        private float ValueToX(int val, Rectangle track)
        {
            float ratio = (float)(val - _minimum) / (float)(_maximum - _minimum);
            return track.Left + ratio * track.Width;
        }

        private int XToValue(int x, bool bypassSnap, out bool snapped)
        {
            Rectangle track = GetTrackRect();
            float ratio = (float)(x - track.Left) / (float)track.Width;
            if (ratio < 0f) ratio = 0f;
            if (ratio > 1f) ratio = 1f;
            int raw = _minimum + (int)Math.Round(ratio * (_maximum - _minimum));
            return ApplySnap(raw, bypassSnap, out snapped);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!Enabled || e.Button != MouseButtons.Left) return;
            Focus();
            _dragging = true;
            UpdateFromMouse(e.X);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_dragging || !Enabled) return;
            UpdateFromMouse(e.X);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!_dragging) return;
            _dragging = false;
            UpdateFromMouse(e.X);
            Invalidate();
            if (ValueCommitted != null) ValueCommitted(this, EventArgs.Empty);
        }

        private void UpdateFromMouse(int mouseX)
        {
            bool bypass = (Control.ModifierKeys & Keys.Shift) == Keys.Shift;
            bool snapped;
            int nextVal = XToValue(mouseX, bypass, out snapped);
            if (nextVal != _value || snapped != _snapped)
            {
                _value = nextVal;
                _snapped = snapped;
                Invalidate();
                if (ValuePreviewChanged != null) ValuePreviewChanged(this, EventArgs.Empty);
            }
        }

        protected override bool IsInputKey(Keys keyData)
        {
            if (keyData == Keys.Left || keyData == Keys.Right || keyData == Keys.PageUp || keyData == Keys.PageDown)
                return true;
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (!Enabled) return;
            int delta = 0;
            if (e.KeyCode == Keys.Left) delta = -1;
            else if (e.KeyCode == Keys.Right) delta = 1;
            else if (e.KeyCode == Keys.PageDown) delta = -10;
            else if (e.KeyCode == Keys.PageUp) delta = 10;
            if (delta != 0)
            {
                int nextVal = Math.Max(_minimum, Math.Min(_maximum, _value + delta));
                if (nextVal != _value)
                {
                    _value = nextVal;
                    _snapped = IsExactSnapPoint(_value);
                    Invalidate();
                    if (ValuePreviewChanged != null) ValuePreviewChanged(this, EventArgs.Empty);
                    if (ValueCommitted != null) ValueCommitted(this, EventArgs.Empty);
                }
                e.Handled = true;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            Rectangle track = GetTrackRect();
            float thumbX = ValueToX(_value, track);
            float trackMidY = track.Top + track.Height / 2f;

            // 底轨
            using (GraphicsPath bgPath = RoundedRect(track, 3))
            using (SolidBrush bgBrush = new SolidBrush(Color.FromArgb(56, 62, 76)))
            {
                g.FillPath(bgBrush, bgPath);
            }

            // 已填充部分
            int fillW = (int)Math.Round(thumbX - track.Left);
            if (fillW > 0)
            {
                Rectangle fillRect = new Rectangle(track.Left, track.Top, fillW, track.Height);
                Color activeColor = Enabled ? _accentColor : Color.FromArgb(100, 108, 122);
                using (GraphicsPath fillPath = RoundedRect(fillRect, 3))
                using (SolidBrush fillBrush = new SolidBrush(activeColor))
                {
                    g.FillPath(fillBrush, fillPath);
                }
            }

            // 磁吸刻度点与数字标签 (20 / 40 / 60 / 80)
            using (Font tickFont = new Font("Consolas", 7.5f, FontStyle.Regular))
            using (SolidBrush tickTextBrush = new SolidBrush(Color.FromArgb(140, 148, 164)))
            using (SolidBrush tickTextActive = new SolidBrush(_accentColor))
            {
                for (int i = 0; i < _snapPoints.Length; i++)
                {
                    int sp = _snapPoints[i];
                    if (sp < _minimum || sp > _maximum) continue;
                    float sx = ValueToX(sp, track);
                    bool isCurrent = (_value == sp);

                    // 轨道上的吸附圆点
                    float dotR = isCurrent ? 3.5f : 2.5f;
                    Color dotColor = isCurrent ? Color.White : (sp <= _value ? Color.FromArgb(255, 245, 220) : Color.FromArgb(128, 136, 152));
                    using (SolidBrush db = new SolidBrush(dotColor))
                    {
                        g.FillEllipse(db, sx - dotR, trackMidY - dotR, dotR * 2f, dotR * 2f);
                    }

                    // 刻度文字
                    string lbl = sp.ToString();
                    SizeF sz = g.MeasureString(lbl, tickFont);
                    g.DrawString(lbl, tickFont, isCurrent ? tickTextActive : tickTextBrush, sx - sz.Width / 2f, track.Bottom + 6f);
                }
            }

            // 滑块圆钮（吸附时带外环反馈）
            float thumbR = _dragging ? 8.5f : 7.5f;
            if (_snapped && _dragging)
            {
                using (SolidBrush glow = new SolidBrush(Color.FromArgb(75, _accentColor)))
                {
                    g.FillEllipse(glow, thumbX - 13f, trackMidY - 13f, 26f, 26f);
                }
            }
            using (SolidBrush thumbFill = new SolidBrush(Enabled ? Color.White : Color.FromArgb(170, 175, 185)))
            using (Pen thumbBorder = new Pen(Enabled ? _accentColor : Color.FromArgb(100, 108, 122), 2.2f))
            {
                g.FillEllipse(thumbFill, thumbX - thumbR, trackMidY - thumbR, thumbR * 2f, thumbR * 2f);
                g.DrawEllipse(thumbBorder, thumbX - thumbR, trackMidY - thumbR, thumbR * 2f, thumbR * 2f);
            }
        }

        private static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int d = radius * 2;
            if (r.Width < d) d = r.Width;
            if (r.Height < d) d = r.Height;
            if (d <= 0)
            {
                path.AddRectangle(r);
                return path;
            }
            path.AddArc(r.Left, r.Top, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    public sealed class MainForm : Form
    {
        private readonly Label _lblMonitor;
        private readonly Label _lblWarnBanner;
        private readonly Label _lblModeValue;
        private readonly Label _lblModeMeta;
        private readonly Button _btnRefresh;
        private readonly Button _btnSrgb;
        private readonly Button _btnFps;
        private readonly Button _btnReset;

        private readonly Label _lblVrrStatus;
        private readonly Button _btnVrrOn;
        private readonly Button _btnVrrOff;

        private readonly Label _lblBrightVal;
        private readonly SnapSlider _sliderBright;
        private readonly List<Button> _brightPresets = new List<Button>();

        private readonly Label _lblVolVal;
        private readonly SnapSlider _sliderVol;
        private readonly List<Button> _volPresets = new List<Button>();

        private readonly TextBox _txtLog;

        private bool _busy;
        private bool _asusConflictAcceptedOnce;
        private int _lastKnownBright = 70;
        private int _lastKnownVol = 100;

        private static readonly Color BgDark = Color.FromArgb(22, 24, 30);
        private static readonly Color CardDark = Color.FromArgb(32, 35, 44);
        private static readonly Color TextPrimary = Color.FromArgb(242, 244, 248);
        private static readonly Color TextMuted = Color.FromArgb(154, 162, 178);
        private static readonly Color AccentBlue = Color.FromArgb(59, 130, 246);
        private static readonly Color AccentPurple = Color.FromArgb(139, 92, 246);
        private static readonly Color AccentAmber = Color.FromArgb(217, 119, 6);
        private static readonly Color AccentGold = Color.FromArgb(245, 158, 11);
        private static readonly Color AccentGreen = Color.FromArgb(16, 185, 129);
        private static readonly Color AccentTeal = Color.FromArgb(20, 184, 166);
        private static readonly Color NeutralBtn = Color.FromArgb(50, 55, 68);
        private static readonly Color PresetBtnBg = Color.FromArgb(44, 49, 60);

        public MainForm()
        {
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = "ASUS 显示器速控工具 (VG27AQML5A)";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            BackColor = BgDark;
            ForeColor = TextPrimary;
            Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Regular);
            ClientSize = new Size(496, 636);
            KeyPreview = true;
            KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape) Close();
            };

            // 顶部栏：显示器状态 + 刷新
            _lblMonitor = new Label();
            _lblMonitor.Text = "显示器：正在读取...";
            _lblMonitor.ForeColor = TextMuted;
            _lblMonitor.Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular);
            _lblMonitor.Location = new Point(18, 16);
            _lblMonitor.Size = new Size(358, 22);
            Controls.Add(_lblMonitor);

            _btnRefresh = CreateButton("刷新全部", NeutralBtn, new Point(386, 11), new Size(92, 30));
            _btnRefresh.Font = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Regular);
            _btnRefresh.Click += delegate { RunReadAllAsync(); };
            Controls.Add(_btnRefresh);

            // 冲突提醒横幅
            _lblWarnBanner = new Label();
            _lblWarnBanner.Text = "提示：华硕 DisplayWidgetCenter 正在后台运行，建议先退出以免抢占 DDC/CI 总线";
            _lblWarnBanner.BackColor = Color.FromArgb(66, 46, 16);
            _lblWarnBanner.ForeColor = Color.FromArgb(253, 224, 71);
            _lblWarnBanner.Font = new Font("Microsoft YaHei UI", 8.2f, FontStyle.Regular);
            _lblWarnBanner.TextAlign = ContentAlignment.MiddleLeft;
            _lblWarnBanner.Padding = new Padding(8, 0, 8, 0);
            _lblWarnBanner.Location = new Point(18, 46);
            _lblWarnBanner.Size = new Size(460, 24);
            _lblWarnBanner.Visible = false;
            Controls.Add(_lblWarnBanner);

            // 卡片 1：显示模式 (GameVisual)
            Panel modeCard = CreateCard(new Point(18, 78), new Size(460, 128));
            Controls.Add(modeCard);

            Label lblModeTitle = new Label();
            lblModeTitle.Text = "当前显示模式 (GameVisual · 0xDC)";
            lblModeTitle.ForeColor = TextMuted;
            lblModeTitle.Font = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Regular);
            lblModeTitle.Location = new Point(14, 10);
            lblModeTitle.Size = new Size(250, 18);
            modeCard.Controls.Add(lblModeTitle);

            _lblModeValue = new Label();
            _lblModeValue.Text = "读取中...";
            _lblModeValue.ForeColor = TextPrimary;
            _lblModeValue.Font = new Font("Microsoft YaHei UI", 15f, FontStyle.Bold);
            _lblModeValue.Location = new Point(12, 28);
            _lblModeValue.Size = new Size(210, 32);
            modeCard.Controls.Add(_lblModeValue);

            _lblModeMeta = new Label();
            _lblModeMeta.Text = "0xDC = --";
            _lblModeMeta.ForeColor = TextMuted;
            _lblModeMeta.Font = new Font("Consolas", 8.5f, FontStyle.Regular);
            _lblModeMeta.TextAlign = ContentAlignment.MiddleRight;
            _lblModeMeta.Location = new Point(222, 36);
            _lblModeMeta.Size = new Size(224, 20);
            modeCard.Controls.Add(_lblModeMeta);

            _btnSrgb = CreateButton("切换 sRGB 模式", AccentBlue, new Point(14, 72), new Size(138, 40));
            _btnSrgb.Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
            _btnSrgb.Click += delegate { RunSwitchModeAsync(TargetDisplayMode.Srgb); };
            modeCard.Controls.Add(_btnSrgb);

            _btnFps = CreateButton("切换 FPS 模式", AccentPurple, new Point(161, 72), new Size(138, 40));
            _btnFps.Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
            _btnFps.Click += delegate { RunSwitchModeAsync(TargetDisplayMode.Fps); };
            modeCard.Controls.Add(_btnFps);

            _btnReset = CreateButton("重置当前模式", AccentAmber, new Point(308, 72), new Size(138, 40));
            _btnReset.Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
            _btnReset.Click += delegate { RunResetCurrentModeAsync(); };
            modeCard.Controls.Add(_btnReset);

            // 卡片 2：可变刷新率 VRR (0xFD bit0)
            Panel vrrCard = CreateCard(new Point(18, 216), new Size(460, 62));
            Controls.Add(vrrCard);

            Label lblVrrTitle = new Label();
            lblVrrTitle.Text = "可变刷新率 (VRR · 0xFD bit0)";
            lblVrrTitle.ForeColor = TextMuted;
            lblVrrTitle.Font = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Regular);
            lblVrrTitle.Location = new Point(14, 9);
            lblVrrTitle.Size = new Size(250, 18);
            vrrCard.Controls.Add(lblVrrTitle);

            _lblVrrStatus = new Label();
            _lblVrrStatus.Text = "读取中...";
            _lblVrrStatus.ForeColor = TextPrimary;
            _lblVrrStatus.Font = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold);
            _lblVrrStatus.Location = new Point(12, 29);
            _lblVrrStatus.Size = new Size(256, 24);
            vrrCard.Controls.Add(_lblVrrStatus);

            _btnVrrOn = CreateButton("开启 VRR", AccentGreen, new Point(276, 14), new Size(82, 34));
            _btnVrrOn.Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);
            _btnVrrOn.Click += delegate { RunSetVrrAsync(true); };
            vrrCard.Controls.Add(_btnVrrOn);

            _btnVrrOff = CreateButton("关闭 VRR", NeutralBtn, new Point(366, 14), new Size(80, 34));
            _btnVrrOff.Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);
            _btnVrrOff.Click += delegate { RunSetVrrAsync(false); };
            vrrCard.Controls.Add(_btnVrrOff);

            // 卡片 3：亮度调节 (0x10)
            Panel brightCard = CreateCard(new Point(18, 288), new Size(460, 114));
            Controls.Add(brightCard);

            Label lblBrightTitle = new Label();
            lblBrightTitle.Text = "显示器亮度 (0x10)  ·  20/40/60/80 磁吸，按住 Shift 微调";
            lblBrightTitle.ForeColor = TextMuted;
            lblBrightTitle.Font = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Regular);
            lblBrightTitle.Location = new Point(14, 10);
            lblBrightTitle.Size = new Size(340, 18);
            brightCard.Controls.Add(lblBrightTitle);

            _lblBrightVal = new Label();
            _lblBrightVal.Text = "--";
            _lblBrightVal.ForeColor = AccentGold;
            _lblBrightVal.Font = new Font("Consolas", 13f, FontStyle.Bold);
            _lblBrightVal.TextAlign = ContentAlignment.MiddleRight;
            _lblBrightVal.Location = new Point(360, 6);
            _lblBrightVal.Size = new Size(86, 26);
            brightCard.Controls.Add(_lblBrightVal);

            _sliderBright = new SnapSlider();
            _sliderBright.Location = new Point(10, 32);
            _sliderBright.Size = new Size(440, 42);
            _sliderBright.AccentColor = AccentGold;
            _sliderBright.SnapPoints = new int[] { 20, 40, 60, 80 };
            _sliderBright.SnapThreshold = 3;
            _sliderBright.ValuePreviewChanged += delegate
            {
                _lblBrightVal.Text = _sliderBright.Value.ToString() + (_sliderBright.IsSnapped ? " ●" : "");
            };
            _sliderBright.ValueCommitted += delegate { RunSetBrightnessAsync(_sliderBright.Value); };
            brightCard.Controls.Add(_sliderBright);

            int[] brightMarks = new int[] { 20, 40, 60, 80, 100 };
            for (int i = 0; i < brightMarks.Length; i++)
            {
                int val = brightMarks[i];
                Button pb = CreateButton(val.ToString(), PresetBtnBg, new Point(14 + i * 88, 78), new Size(80, 26));
                pb.Font = new Font("Consolas", 8.8f, FontStyle.Bold);
                pb.Click += delegate { RunSetBrightnessAsync(val); };
                brightCard.Controls.Add(pb);
                _brightPresets.Add(pb);
            }

            // 卡片 4：显示器音量 (0x62)
            Panel volCard = CreateCard(new Point(18, 412), new Size(460, 114));
            Controls.Add(volCard);

            Label lblVolTitle = new Label();
            lblVolTitle.Text = "显示器音量 (0x62)  ·  20/40/60/80 磁吸，按住 Shift 微调";
            lblVolTitle.ForeColor = TextMuted;
            lblVolTitle.Font = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Regular);
            lblVolTitle.Location = new Point(14, 10);
            lblVolTitle.Size = new Size(340, 18);
            volCard.Controls.Add(lblVolTitle);

            _lblVolVal = new Label();
            _lblVolVal.Text = "--";
            _lblVolVal.ForeColor = AccentTeal;
            _lblVolVal.Font = new Font("Consolas", 13f, FontStyle.Bold);
            _lblVolVal.TextAlign = ContentAlignment.MiddleRight;
            _lblVolVal.Location = new Point(360, 6);
            _lblVolVal.Size = new Size(86, 26);
            volCard.Controls.Add(_lblVolVal);

            _sliderVol = new SnapSlider();
            _sliderVol.Location = new Point(10, 32);
            _sliderVol.Size = new Size(440, 42);
            _sliderVol.AccentColor = AccentTeal;
            _sliderVol.SnapPoints = new int[] { 20, 40, 60, 80 };
            _sliderVol.SnapThreshold = 3;
            _sliderVol.ValuePreviewChanged += delegate
            {
                _lblVolVal.Text = _sliderVol.Value.ToString() + (_sliderVol.IsSnapped ? " ●" : "");
            };
            _sliderVol.ValueCommitted += delegate { RunSetVolumeAsync(_sliderVol.Value); };
            volCard.Controls.Add(_sliderVol);

            int[] volMarks = new int[] { 0, 20, 40, 60, 80, 100 };
            for (int i = 0; i < volMarks.Length; i++)
            {
                int val = volMarks[i];
                string cap = val == 0 ? "0 静音" : val.ToString();
                Button pb = CreateButton(cap, PresetBtnBg, new Point(14 + i * 73, 78), new Size(67, 26));
                pb.Font = new Font("Microsoft YaHei UI", 8.2f, FontStyle.Bold);
                pb.Click += delegate { RunSetVolumeAsync(val); };
                volCard.Controls.Add(pb);
                _volPresets.Add(pb);
            }

            // 底部日志
            _txtLog = new TextBox();
            _txtLog.Multiline = true;
            _txtLog.ReadOnly = true;
            _txtLog.ScrollBars = ScrollBars.Vertical;
            _txtLog.BackColor = Color.FromArgb(16, 18, 23);
            _txtLog.ForeColor = Color.FromArgb(196, 204, 218);
            _txtLog.BorderStyle = BorderStyle.FixedSingle;
            _txtLog.Font = new Font("Consolas", 8.3f, FontStyle.Regular);
            _txtLog.Location = new Point(18, 538);
            _txtLog.Size = new Size(460, 84);
            Controls.Add(_txtLog);

            Shown += delegate { RunReadAllAsync(); };
            ResumeLayout(false);
        }

        private static Panel CreateCard(Point loc, Size size)
        {
            Panel p = new Panel();
            p.BackColor = CardDark;
            p.Location = loc;
            p.Size = size;
            return p;
        }

        private static Button CreateButton(string text, Color bg, Point loc, Size size)
        {
            Button btn = new Button();
            btn.Text = text;
            btn.BackColor = bg;
            btn.ForeColor = Color.White;
            btn.FlatStyle = FlatStyle.Flat;
            btn.FlatAppearance.BorderSize = 0;
            btn.Cursor = Cursors.Hand;
            btn.Location = loc;
            btn.Size = size;
            return btn;
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            _btnRefresh.Enabled = !busy;
            _btnSrgb.Enabled = !busy;
            _btnFps.Enabled = !busy;
            _btnReset.Enabled = !busy;
            _btnVrrOn.Enabled = !busy;
            _btnVrrOff.Enabled = !busy;
            _sliderBright.Enabled = !busy;
            _sliderVol.Enabled = !busy;
            for (int i = 0; i < _brightPresets.Count; i++) _brightPresets[i].Enabled = !busy;
            for (int i = 0; i < _volPresets.Count; i++) _volPresets[i].Enabled = !busy;
        }

        private void AppendLog(string msg)
        {
            string line = string.Format("[{0:HH:mm:ss}] {1}", DateTime.Now, msg);
            if (_txtLog.TextLength > 0) _txtLog.AppendText(Environment.NewLine);
            _txtLog.AppendText(line);
        }

        private void UpdateConflictBanner()
        {
            bool running = DdcController.IsAsusWidgetRunning();
            _lblWarnBanner.Visible = running;
            if (!running) _asusConflictAcceptedOnce = false;
        }

        private bool ConfirmIfAsusRunning(string actionLabel)
        {
            UpdateConflictBanner();
            if (!DdcController.IsAsusWidgetRunning() || _asusConflictAcceptedOnce)
            {
                return true;
            }
            DialogResult dr = MessageBox.Show(
                this,
                string.Format("检测到华硕 DisplayWidgetCenter.exe 正在后台运行。\n\n建议先在右下角托盘退出它再执行「{0}」，以避免争抢 DDC/CI 总线。\n\n是否仍要继续？（本次打开不再重复提示）", actionLabel),
                "DDC/CI 总线冲突提醒",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (dr == DialogResult.Yes)
            {
                _asusConflictAcceptedOnce = true;
                return true;
            }
            return false;
        }

        private void ApplySnapshot(MonitorSnapshot s)
        {
            UpdateConflictBanner();
            if (s == null) return;

            if (!s.Success)
            {
                _lblMonitor.Text = "目标显示器：未匹配 / 读取失败";
                _lblMonitor.ForeColor = Color.FromArgb(252, 165, 165);
                _lblModeValue.Text = "读取失败";
                _lblModeMeta.Text = "请检查显示器 DDC/CI";
                _lblVrrStatus.Text = "读取失败";
                _lblVrrStatus.ForeColor = Color.FromArgb(252, 165, 165);
                _lblBrightVal.Text = "--";
                _lblVolVal.Text = "--";
                return;
            }

            _lblMonitor.Text = string.Format("目标显示器：{0}（已锁定白名单 ✓）", s.MonitorName);
            _lblMonitor.ForeColor = Color.FromArgb(134, 239, 172);

            if (s.ModeOk)
            {
                _lblModeValue.Text = s.ModeName;
                _lblModeMeta.Text = string.Format("0xDC=0x{0:X4} (高=0x{1:X2} 低=0x{2:X2})", s.ModeRaw, s.ModeHighByte, s.ModeLowByte);
                // 高亮当前激活的模式按钮
                _btnSrgb.FlatAppearance.BorderSize = (s.ModeLowByte == (byte)TargetDisplayMode.Srgb) ? 2 : 0;
                _btnSrgb.FlatAppearance.BorderColor = Color.White;
                _btnFps.FlatAppearance.BorderSize = (s.ModeLowByte == (byte)TargetDisplayMode.Fps) ? 2 : 0;
                _btnFps.FlatAppearance.BorderColor = Color.White;
            }
            else
            {
                _lblModeValue.Text = "读取失败";
                _lblModeMeta.Text = "0xDC 读取失败";
            }

            if (s.VrrOk)
            {
                _lblVrrStatus.Text = string.Format("{0}  (0xFD=0x{1:X4})", s.VrrEnabled ? "已开启" : "已关闭", s.VrrRaw);
                _lblVrrStatus.ForeColor = s.VrrEnabled ? Color.FromArgb(110, 231, 183) : TextMuted;
                _btnVrrOn.FlatAppearance.BorderSize = s.VrrEnabled ? 2 : 0;
                _btnVrrOn.FlatAppearance.BorderColor = Color.White;
                _btnVrrOff.FlatAppearance.BorderSize = !s.VrrEnabled ? 2 : 0;
                _btnVrrOff.FlatAppearance.BorderColor = Color.White;
            }
            else
            {
                _lblVrrStatus.Text = "不可用 / 读取失败";
                _lblVrrStatus.ForeColor = TextMuted;
                _btnVrrOn.FlatAppearance.BorderSize = 0;
                _btnVrrOff.FlatAppearance.BorderSize = 0;
            }

            if (s.BrightnessOk)
            {
                _lastKnownBright = (int)s.Brightness;
                _sliderBright.Maximum = (int)s.BrightnessMax;
                _sliderBright.Value = (int)s.Brightness;
                _lblBrightVal.Text = string.Format("{0}/{1}", s.Brightness, s.BrightnessMax);
            }
            else
            {
                _lblBrightVal.Text = "N/A";
            }

            if (s.VolumeOk)
            {
                _lastKnownVol = (int)s.Volume;
                _sliderVol.Maximum = (int)s.VolumeMax;
                _sliderVol.Value = (int)s.Volume;
                _lblVolVal.Text = string.Format("{0}/{1}", s.Volume, s.VolumeMax);
            }
            else
            {
                _lblVolVal.Text = "N/A";
            }
        }

        private async void RunReadAllAsync()
        {
            if (_busy) return;
            SetBusy(true);
            try
            {
                MonitorSnapshot snap = await Task.Run(delegate { return DdcController.ReadAll(); });
                ApplySnapshot(snap);
                if (snap.Success)
                {
                    AppendLog(string.Format("读取完成：{0} | 模式={1}(0x{2:X4}) | VRR={3} | 亮度={4} | 音量={5} | {6}ms",
                        snap.MonitorName,
                        snap.ModeOk ? snap.ModeName : "?",
                        snap.ModeRaw,
                        snap.VrrOk ? (snap.VrrEnabled ? "开" : "关") : "?",
                        snap.BrightnessOk ? snap.Brightness.ToString() : "?",
                        snap.VolumeOk ? snap.Volume.ToString() : "?",
                        snap.ElapsedMs));
                }
                else
                {
                    AppendLog("读取失败：" + snap.ErrorMessage);
                }
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async void RunSwitchModeAsync(TargetDisplayMode target)
        {
            if (_busy) return;
            string label = DdcController.FormatModeName((byte)target);
            if (!ConfirmIfAsusRunning("切换为 " + label + " 模式")) return;

            SetBusy(true);
            try
            {
                MonitorWriteResult w = await Task.Run(delegate { return DdcController.SwitchDisplayMode(target); });
                if (w.Snapshot != null) ApplySnapshot(w.Snapshot);
                AppendLog(w.Message);
                if (!w.Success) MessageBox.Show(this, w.Message, "切换失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async void RunResetCurrentModeAsync()
        {
            if (_busy) return;
            if (!ConfirmIfAsusRunning("重置当前显示模式")) return;

            DialogResult confirm = MessageBox.Show(
                this,
                "确定要重置当前显示模式的参数吗？\n\n将发送华硕官方指令：VCP 0xEC = 1（RestoreModeDefaults）。\n仅恢复当前 GameVisual 模式的画面默认参数，不会执行整机出厂重置（0x04）。",
                "确认重置当前显示模式",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);
            if (confirm != DialogResult.Yes) return;

            SetBusy(true);
            try
            {
                MonitorWriteResult w = await Task.Run(delegate { return DdcController.ResetCurrentMode(); });
                if (w.Snapshot != null) ApplySnapshot(w.Snapshot);
                AppendLog(w.Message);
                if (!w.Success) MessageBox.Show(this, w.Message, "重置失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async void RunSetBrightnessAsync(int value)
        {
            if (_busy) return;
            if (!ConfirmIfAsusRunning("调节亮度"))
            {
                _sliderBright.Value = _lastKnownBright;
                _lblBrightVal.Text = string.Format("{0}/{1}", _lastKnownBright, _sliderBright.Maximum);
                return;
            }

            SetBusy(true);
            try
            {
                MonitorWriteResult w = await Task.Run(delegate { return DdcController.SetBrightness(value); });
                if (w.Snapshot != null) ApplySnapshot(w.Snapshot);
                else { _sliderBright.Value = _lastKnownBright; }
                AppendLog(w.Message);
                if (!w.Success) MessageBox.Show(this, w.Message, "调节亮度失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async void RunSetVolumeAsync(int value)
        {
            if (_busy) return;
            if (!ConfirmIfAsusRunning("调节显示器音量"))
            {
                _sliderVol.Value = _lastKnownVol;
                _lblVolVal.Text = string.Format("{0}/{1}", _lastKnownVol, _sliderVol.Maximum);
                return;
            }

            SetBusy(true);
            try
            {
                MonitorWriteResult w = await Task.Run(delegate { return DdcController.SetVolume(value); });
                if (w.Snapshot != null) ApplySnapshot(w.Snapshot);
                else { _sliderVol.Value = _lastKnownVol; }
                AppendLog(w.Message);
                if (!w.Success) MessageBox.Show(this, w.Message, "调节音量失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async void RunSetVrrAsync(bool enable)
        {
            if (_busy) return;
            string actionLabel = enable ? "开启可变刷新率 (VRR)" : "关闭可变刷新率 (VRR)";
            if (!ConfirmIfAsusRunning(actionLabel)) return;

            SetBusy(true);
            AppendLog(string.Format("正在{0}（屏幕可能会短暂黑屏重握手 1~2 秒）...", actionLabel));
            try
            {
                MonitorWriteResult w = await Task.Run(delegate { return DdcController.SetVrr(enable); });
                if (w.Snapshot != null) ApplySnapshot(w.Snapshot);
                AppendLog(w.Message);
                if (!w.Success) MessageBox.Show(this, w.Message, "切换 VRR 失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                SetBusy(false);
            }
        }
    }

    internal static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [STAThread]
        private static int Main(string[] args)
        {
            if (args != null && args.Length >= 2 && string.Equals(args[0], "--status-out", StringComparison.OrdinalIgnoreCase))
            {
                MonitorSnapshot s = DdcController.ReadAll();
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("Success=" + s.Success);
                sb.AppendLine("Monitor=" + (s.MonitorName ?? ""));
                sb.AppendLine("Handle=" + s.MonitorHandleValue);
                sb.AppendLine("ModeOk=" + s.ModeOk + " Mode=" + (s.ModeName ?? "") + string.Format(" Raw=0x{0:X4}", s.ModeRaw));
                sb.AppendLine("BrightnessOk=" + s.BrightnessOk + " Brightness=" + s.Brightness + "/" + s.BrightnessMax);
                sb.AppendLine("VolumeOk=" + s.VolumeOk + " Volume=" + s.Volume + "/" + s.VolumeMax);
                sb.AppendLine("VrrOk=" + s.VrrOk + " VrrEnabled=" + s.VrrEnabled + string.Format(" VrrRaw=0x{0:X4} VrrMax=0x{1:X4}", s.VrrRaw, s.VrrMax));
                sb.AppendLine("ElapsedMs=" + s.ElapsedMs);
                sb.AppendLine("AsusWidgetRunning=" + DdcController.IsAsusWidgetRunning());
                if (!string.IsNullOrEmpty(s.ErrorMessage)) sb.AppendLine("Error=" + s.ErrorMessage);
                System.IO.File.WriteAllText(args[1], sb.ToString(), Encoding.UTF8);
                return s.Success ? 0 : 1;
            }

            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
            return 0;
        }
    }
}
