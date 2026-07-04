#include <windows.h>
#include <tlhelp32.h>
#include <psapi.h>
#include <shlobj.h>
#include <shellapi.h>
#include <conio.h>
#include <iostream>
#include <string>
#include <vector>
#include <unordered_set>
#include <unordered_map>
#include <algorithm>
#include <chrono>
#include <fstream>
#include <cwctype>

// Định nghĩa con trỏ hàm cho NtSuspendProcess và NtResumeProcess từ ntdll.dll
typedef LONG(NTAPI* pfnNtSuspendProcess)(HANDLE ProcessHandle);
typedef LONG(NTAPI* pfnNtResumeProcess)(HANDLE ProcessHandle);

pfnNtSuspendProcess NtSuspendProcess = nullptr;
pfnNtResumeProcess NtResumeProcess = nullptr;

// Các tầng ưu tiên (MLFQ Tiers)
enum PriorityTier {
    TIER_1_REALTIME = 1,  // Tầng 1: CHỈ dành cho duy nhất tác vụ đang hoạt động trên màn hình (Foreground)
    TIER_2_NORMAL = 2,    // Tầng 2: Chạy nền tiết kiệm (User Whitelist / Promoted)
    TIER_3_LOW = 3        // Tầng 3: Bị giới hạn tối đa (Đóng băng hoặc Hệ thống chạy ở mức IDLE trên Core 1)
};

// Theo dõi trạng thái tiến trình
struct ProcessState {
    DWORD pid;
    std::wstring name;
    PriorityTier tier;
    std::chrono::steady_clock::time_point lastStateChangeTime;
    bool isSuspended;
    bool isTemporarilyPromoted;
    std::chrono::steady_clock::time_point promotionStartTime;
};

std::unordered_set<std::wstring> g_UserWhitelist;
std::unordered_map<DWORD, ProcessState> g_ProcessStates;
DWORD g_SelfPid = 0;

// CPU Core Masks
DWORD_PTR g_ForegroundMask = 3;  // Core 0 + Core 1 (cho Tầng 1)
DWORD_PTR g_BackgroundMask = 2;  // Chỉ chạy trên Core 1 (cho Tầng 2 & Tầng 3)

// Danh sách các tiến trình hệ thống cốt lõi KHÔNG THỂ ĐÓNG BĂNG (để tránh xanh màn hình / crash Windows)
// Nhưng sẽ bị giới hạn độ ưu tiên ở mức IDLE và chỉ chạy trên Core 1 (Tầng 3)
const std::unordered_set<std::wstring> g_CriticalSystemList = {
    L"idle", L"system", L"smss.exe", L"csrss.exe", L"wininit.exe",
    L"services.exe", L"lsass.exe", L"winlogon.exe", L"svchost.exe",
    L"dwm.exe", L"explorer.exe", L"conhost.exe", L"cmd.exe",
    L"powershell.exe", L"taskmgr.exe", L"spoolsv.exe", L"ctfmon.exe",
    L"runtimebroker.exe", L"audiodg.exe", L"logonui.exe", L"wmiprvse.exe",
    L"fontdrvhost.exe", L"registry", L"memory compression"
};

const wchar_t* g_StartupKeyName = L"Neko_Cpu_Optimizer";

std::wstring ToLower(std::wstring str) {
    std::transform(str.begin(), str.end(), str.begin(), ::towlower);
    return str;
}

// Nạp whitelist của người dùng từ file txt
void LoadWhitelist() {
    g_UserWhitelist.clear();
    std::ifstream file("whitelist.txt");
    if (file.is_open()) {
        std::string line;
        while (std::getline(file, line)) {
            line.erase(std::remove(line.begin(), line.end(), '\r'), line.end());
            line.erase(std::remove(line.begin(), line.end(), '\n'), line.end());
            if (!line.empty()) {
                std::wstring wline(line.begin(), line.end());
                g_UserWhitelist.insert(ToLower(wline));
            }
        }
        file.close();
    } else {
        std::ofstream outfile("whitelist.txt");
        if (outfile.is_open()) {
            outfile << "chrome.exe\n";
            outfile << "discord.exe\n";
            outfile << "spotify.exe\n";
            outfile << "zalo.exe\n";
            outfile.close();
        }
        g_UserWhitelist.insert(L"chrome.exe");
        g_UserWhitelist.insert(L"discord.exe");
        g_UserWhitelist.insert(L"spotify.exe");
        g_UserWhitelist.insert(L"zalo.exe");
    }
}

bool InitializeSystem() {
    HMODULE hNtdll = GetModuleHandleA("ntdll.dll");
    if (!hNtdll) hNtdll = LoadLibraryA("ntdll.dll");
    if (hNtdll) {
        NtSuspendProcess = (pfnNtSuspendProcess)GetProcAddress(hNtdll, "NtSuspendProcess");
        NtResumeProcess = (pfnNtResumeProcess)GetProcAddress(hNtdll, "NtResumeProcess");
    }

    g_SelfPid = GetCurrentProcessId();

    SYSTEM_INFO sysInfo;
    GetSystemInfo(&sysInfo);
    DWORD numCores = sysInfo.dwNumberOfProcessors;
    if (numCores >= 4) {
        // Tầng 1: 75% cores (4 cores -> mask 0b0111 = 7)
        DWORD_PTR t1Cores = (DWORD_PTR)ceil(numCores * 0.75);
        g_ForegroundMask = (1ULL << t1Cores) - 1;
        // Tầng 2: ~1.5 core hiệu quả -> core 1+2 = 0b0110 = 6 + BELOW_NORMAL priority
        g_BackgroundMask = 6; // Core 1 + Core 2
    } else if (numCores > 1) {
        g_ForegroundMask = (1ULL << numCores) - 1;
        g_BackgroundMask = g_ForegroundMask & ~1ULL;
    } else {
        g_ForegroundMask = 1;
        g_BackgroundMask = 1;
    }

    LoadWhitelist();
    return (NtSuspendProcess != nullptr && NtResumeProcess != nullptr);
}

std::wstring GetProcessPath(DWORD pid) {
    std::wstring pathStr = L"";
    HANDLE hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
    if (hProcess) {
        wchar_t path[MAX_PATH];
        DWORD size = MAX_PATH;
        if (QueryFullProcessImageNameW(hProcess, 0, path, &size)) {
            pathStr = path;
        }
        CloseHandle(hProcess);
    }
    return pathStr;
}

// Kiểm tra tiến trình hệ thống cốt lõi
bool IsCriticalSystemProcess(DWORD pid, const std::wstring& name) {
    if (pid == 0 || pid == 4 || pid == g_SelfPid) return true;
    std::wstring nameLower = ToLower(name);
    return g_CriticalSystemList.find(nameLower) != g_CriticalSystemList.end();
}

// Khôi phục tất cả tiến trình về bình thường
void ResumeAndResetAll() {
    if (g_ProcessStates.empty()) return;

    for (auto& pair : g_ProcessStates) {
        ProcessState& state = pair.second;
        if (state.isSuspended) {
            HANDLE hProcess = OpenProcess(PROCESS_SUSPEND_RESUME, FALSE, state.pid);
            if (hProcess) {
                if (NtResumeProcess) NtResumeProcess(hProcess);
                CloseHandle(hProcess);
            }
            state.isSuspended = false;
        }

        HANDLE hProcess = OpenProcess(PROCESS_SET_INFORMATION, FALSE, state.pid);
        if (hProcess) {
            SetPriorityClass(hProcess, NORMAL_PRIORITY_CLASS);
            SetProcessAffinityMask(hProcess, g_ForegroundMask);
            CloseHandle(hProcess);
        }
    }
    g_ProcessStates.clear();
}

bool RegisterStartup(bool enable) {
    HKEY hKey = NULL;
    LONG lResult = RegOpenKeyExW(HKEY_CURRENT_USER, L"Software\\Microsoft\\Windows\\CurrentVersion\\Run", 0, KEY_WRITE, &hKey);
    if (lResult != ERROR_SUCCESS) return false;

    bool success = false;
    if (enable) {
        wchar_t szPath[MAX_PATH];
        GetModuleFileNameW(NULL, szPath, MAX_PATH);
        std::wstring startCmd = L"\"" + std::wstring(szPath) + L"\" --startup";
        lResult = RegSetValueExW(hKey, g_StartupKeyName, 0, REG_SZ, (BYTE*)startCmd.c_str(), (startCmd.length() + 1) * sizeof(wchar_t));
        success = (lResult == ERROR_SUCCESS);
    } else {
        lResult = RegDeleteValueW(hKey, g_StartupKeyName);
        success = (lResult == ERROR_SUCCESS || lResult == ERROR_FILE_NOT_FOUND);
    }

    RegCloseKey(hKey);
    return success;
}

bool IsStartupEnabled() {
    HKEY hKey = NULL;
    LONG lResult = RegOpenKeyExW(HKEY_CURRENT_USER, L"Software\\Microsoft\\Windows\\CurrentVersion\\Run", 0, KEY_READ, &hKey);
    if (lResult != ERROR_SUCCESS) return false;

    lResult = RegQueryValueExW(hKey, g_StartupKeyName, NULL, NULL, NULL, NULL);
    RegCloseKey(hKey);
    return (lResult == ERROR_SUCCESS);
}

void UpdateConsoleTitle(size_t suspendedCount, bool isPaused) {
    wchar_t title[128];
    swprintf_s(title, L"[MLFQ Optimizer] Dong bang: %zu | %s", 
        suspendedCount, 
        isPaused ? L"TAM DUNG" : L"HOAT DONG"
    );
    SetConsoleTitleW(title);
}

// Bước tối ưu hóa MLFQ
void ProcessOptimizationStep() {
    auto now = std::chrono::steady_clock::now();

    // Lấy tiến trình trên màn hình (Tầng 1)
    HWND hwnd = GetForegroundWindow();
    if (!hwnd) return;

    DWORD foregroundPid = 0;
    GetWindowThreadProcessId(hwnd, &foregroundPid);
    if (foregroundPid == 0) return;

    std::wstring fgPath = GetProcessPath(foregroundPid);
    std::wstring fgName = L"";
    size_t lastSlash = fgPath.find_last_of(L"\\");
    if (lastSlash != std::wstring::npos) fgName = fgPath.substr(lastSlash + 1);
    else fgName = fgPath;

    // Kiểm tra xem foreground có phải là core system không
    bool isFgCriticalSystem = IsCriticalSystemProcess(foregroundPid, fgName);

    std::unordered_set<DWORD> activePids;
    HANDLE hSnapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (hSnapshot != INVALID_HANDLE_VALUE) {
        PROCESSENTRY32W pe;
        pe.dwSize = sizeof(PROCESSENTRY32W);

        if (Process32FirstW(hSnapshot, &pe)) {
            do {
                activePids.insert(pe.th32ProcessID);

                // Không tự quản lý chính mình
                if (pe.th32ProcessID == g_SelfPid) {
                    continue;
                }

                bool isCriticalSystem = IsCriticalSystemProcess(pe.th32ProcessID, pe.szExeFile);
                std::wstring nameLower = ToLower(pe.szExeFile);
                bool isWhitelisted = (g_UserWhitelist.find(nameLower) != g_UserWhitelist.end());

                // Khởi tạo state nếu chưa theo dõi
                if (g_ProcessStates.find(pe.th32ProcessID) == g_ProcessStates.end()) {
                    ProcessState newState;
                    newState.pid = pe.th32ProcessID;
                    newState.name = pe.szExeFile;
                    newState.isSuspended = false;
                    newState.isTemporarilyPromoted = false;
                    
                    if (pe.th32ProcessID == foregroundPid) {
                        newState.tier = TIER_1_REALTIME;
                    } else if (isCriticalSystem) {
                        newState.tier = TIER_3_LOW; // Giới hạn hệ thống xuống Tầng 3
                    } else if (isWhitelisted) {
                        newState.tier = TIER_2_NORMAL;
                    } else {
                        newState.tier = TIER_2_NORMAL; // Khởi đầu T2 để chờ giáng cấp sau 10s
                    }
                    newState.lastStateChangeTime = now;
                    g_ProcessStates[pe.th32ProcessID] = newState;
                }

                ProcessState& state = g_ProcessStates[pe.th32ProcessID];

                // --- QUẢN LÝ THEO PHÂN CẤP MLFQ MỚI ---

                if (pe.th32ProcessID == foregroundPid) {
                    // TẦNG 1: DUY NHẤT TÁC VỤ ĐANG HOẠT ĐỘNG TRÊN MÀN HÌNH
                    if (state.tier != TIER_1_REALTIME) {
                        if (state.isSuspended) {
                            HANDLE hProc = OpenProcess(PROCESS_SUSPEND_RESUME, FALSE, pe.th32ProcessID);
                            if (hProc) {
                                if (NtResumeProcess) NtResumeProcess(hProc);
                                CloseHandle(hProc);
                            }
                            state.isSuspended = false;
                        }

                        // Đẩy lên quyền cao nhất, chạy cả 2 nhân
                        HANDLE hProc = OpenProcess(PROCESS_SET_INFORMATION, FALSE, pe.th32ProcessID);
                        if (hProc) {
                            SetPriorityClass(hProc, HIGH_PRIORITY_CLASS);
                            SetProcessAffinityMask(hProc, g_ForegroundMask);
                            CloseHandle(hProc);
                        }

                        state.tier = TIER_1_REALTIME;
                        state.isTemporarilyPromoted = false;
                        state.lastStateChangeTime = now;
                    }
                } 
                else {
                    // CÁC TÁC VỤ CHẠY NỀN (KỂ CẢ HỆ THỐNG)

                    if (state.tier == TIER_1_REALTIME) {
                        // Vừa mất tiêu điểm -> Giáng xuống T2 hoặc T3
                        state.isTemporarilyPromoted = false;
                        state.lastStateChangeTime = now;
                        
                        if (isCriticalSystem) {
                            state.tier = TIER_3_LOW;
                        } else {
                            state.tier = TIER_2_NORMAL;
                        }
                    }

                    if (isCriticalSystem) {
                        // TÁC VỤ HỆ THỐNG CỐT LÕI: GIỚI HẠN Ở TẦNG 3
                        // Không đóng băng (suspend) tránh lỗi hệ thống, nhưng bóp hiệu năng tối đa:
                        // Cho chạy ở mức IDLE_PRIORITY_CLASS và DUY NHẤT trên Core 1, đồng thời xả RAM liên tục.
                        HANDLE hProc = OpenProcess(PROCESS_SET_INFORMATION | PROCESS_SET_QUOTA, FALSE, pe.th32ProcessID);
                        if (hProc) {
                            SetPriorityClass(hProc, IDLE_PRIORITY_CLASS);
                            SetProcessAffinityMask(hProc, g_BackgroundMask);
                            EmptyWorkingSet(hProc);
                            CloseHandle(hProc);
                        }
                        state.tier = TIER_3_LOW;
                    } 
                    else {
                        // TÁC VỤ KHÔNG PHẢI HỆ THỐNG (USER APPS)
                        if (state.tier == TIER_2_NORMAL) {
                            if (isWhitelisted) {
                                // Whitelist: Giữ ở Tầng 2, BELOW_NORMAL + BackgroundMask (~1.5 core hiệu quả), dọn RAM
                                HANDLE hProc = OpenProcess(PROCESS_SET_INFORMATION | PROCESS_SET_QUOTA, FALSE, pe.th32ProcessID);
                                if (hProc) {
                                    SetPriorityClass(hProc, BELOW_NORMAL_PRIORITY_CLASS);
                                    SetProcessAffinityMask(hProc, g_BackgroundMask);
                                    EmptyWorkingSet(hProc);
                                    CloseHandle(hProc);
                                }
                            } 
                            else if (state.isTemporarilyPromoted) {
                                // Hết 3 giây chạy thử -> Giáng về Tầng 3 (Đóng băng)
                                auto duration = std::chrono::duration_cast<std::chrono::seconds>(now - state.promotionStartTime).count();
                                if (duration >= 3) {
                                    HANDLE hProc = OpenProcess(PROCESS_SUSPEND_RESUME | PROCESS_SET_QUOTA, FALSE, pe.th32ProcessID);
                                    if (hProc) {
                                        if (NtSuspendProcess) {
                                            NtSuspendProcess(hProc);
                                            state.isSuspended = true;
                                            EmptyWorkingSet(hProc);
                                        }
                                        CloseHandle(hProc);
                                    }
                                    state.tier = TIER_3_LOW;
                                    state.isTemporarilyPromoted = false;
                                    state.lastStateChangeTime = now;
                                }
                            } 
                            else {
                                // Chờ 600 giây (10 phút) ở Tầng 2 -> Giáng xuống Tầng 3 (Đóng băng)
                                // Thiết lập BELOW_NORMAL + BackgroundMask trong khi chờ
                                HANDLE hProcWait = OpenProcess(PROCESS_SET_INFORMATION, FALSE, pe.th32ProcessID);
                                if (hProcWait) {
                                    SetPriorityClass(hProcWait, BELOW_NORMAL_PRIORITY_CLASS);
                                    SetProcessAffinityMask(hProcWait, g_BackgroundMask);
                                    CloseHandle(hProcWait);
                                }
                                auto duration = std::chrono::duration_cast<std::chrono::seconds>(now - state.lastStateChangeTime).count();
                                if (duration >= 600) {
                                    HANDLE hProc = OpenProcess(PROCESS_SUSPEND_RESUME | PROCESS_SET_QUOTA, FALSE, pe.th32ProcessID);
                                    if (hProc) {
                                        if (NtSuspendProcess) {
                                            NtSuspendProcess(hProc);
                                            state.isSuspended = true;
                                            EmptyWorkingSet(hProc);
                                        }
                                        CloseHandle(hProc);
                                    }
                                    state.tier = TIER_3_LOW;
                                    state.lastStateChangeTime = now;
                                }
                            }
                        } 
                        else if (state.tier == TIER_3_LOW) {
                            // Thăng chức tạm thời (Aging/Promotion) cho app bị đóng băng sau 45s
                            auto duration = std::chrono::duration_cast<std::chrono::seconds>(now - state.lastStateChangeTime).count();
                            if (duration >= 45) {
                                HANDLE hProc = OpenProcess(PROCESS_SUSPEND_RESUME | PROCESS_SET_INFORMATION, FALSE, pe.th32ProcessID);
                                if (hProc) {
                                    if (NtResumeProcess) {
                                        NtResumeProcess(hProc);
                                        state.isSuspended = false;
                                    }
                                    SetPriorityClass(hProc, IDLE_PRIORITY_CLASS);
                                    SetProcessAffinityMask(hProc, g_BackgroundMask);
                                    CloseHandle(hProc);
                                }
                                state.tier = TIER_2_NORMAL;
                                state.isTemporarilyPromoted = true;
                                state.promotionStartTime = now;
                                state.lastStateChangeTime = now;
                            }
                        }
                    }
                }

            } while (Process32NextW(hSnapshot, &pe));
        }
        CloseHandle(hSnapshot);
    }

    // Dọn dẹp dead processes
    std::vector<DWORD> toRemove;
    for (auto& pair : g_ProcessStates) {
        if (activePids.find(pair.first) == activePids.end()) {
            toRemove.push_back(pair.first);
        }
    }
    for (DWORD pid : toRemove) {
        g_ProcessStates.erase(pid);
    }
}

// Xử lý CTRL + C hoặc click [X]
BOOL WINAPI ConsoleCtrlHandler(DWORD ctrlType) {
    if (ctrlType == CTRL_C_EVENT || ctrlType == CTRL_CLOSE_EVENT || 
        ctrlType == CTRL_BREAK_EVENT || ctrlType == CTRL_LOGOFF_EVENT || 
        ctrlType == CTRL_SHUTDOWN_EVENT) {
        ResumeAndResetAll();
        return TRUE;
    }
    return FALSE;
}

int wmain(int argc, wchar_t* argv[]) {
    std::wcout.imbue(std::locale(""));

    // 1. TỰ ĐỘNG NÂNG QUYỀN ADMIN CAO NHẤT
    if (!IsUserAnAdmin()) {
        wchar_t szPath[MAX_PATH];
        GetModuleFileNameW(NULL, szPath, MAX_PATH);

        SHELLEXECUTEINFOW sei = { sizeof(sei) };
        sei.lpVerb = L"runas";
        sei.lpFile = szPath;
        sei.hwnd = NULL;
        sei.nShow = SW_SHOWNORMAL;

        std::wstring args = L"";
        for (int i = 1; i < argc; i++) args += std::wstring(argv[i]) + L" ";
        if (!args.empty()) sei.lpParameters = args.c_str();

        if (ShellExecuteExW(&sei)) return 0;
        else {
            std::wcout << L"[-] LOI: Can cap quyen Administrator de chay ung dung." << std::endl;
            system("pause");
            return 1;
        }
    }

    // 2. CHECK STARTUP PARAMETER
    bool isStartupMode = false;
    for (int i = 1; i < argc; i++) {
        if (std::wstring(argv[i]) == L"--startup") {
            isStartupMode = true;
            break;
        }
    }

    if (isStartupMode) {
        HWND hwndConsole = GetConsoleWindow();
        if (hwndConsole) ShowWindow(hwndConsole, SW_MINIMIZE);
    }

    if (!InitializeSystem()) {
        std::wcout << L"[-] LOI: Khong the khoi tao he thong an." << std::endl;
        system("pause");
        return 1;
    }

    // Tự động đăng ký startup
    if (!IsStartupEnabled()) {
        RegisterStartup(true);
    }

    SetConsoleCtrlHandler(ConsoleCtrlHandler, TRUE);

    std::wcout << L"==================================================" << std::endl;
    std::wcout << L" hello user!                                      " << std::endl;
    std::wcout << L"==================================================" << std::endl;
    std::wcout << L"[+] Trang thai: working" << std::endl;
    std::wcout << L"[i] Phim tat dieu khien nhanh:" << std::endl;
    std::wcout << L"    - [SPACE] (Phim Cach) : stop / continue"        << std::endl;
    std::wcout << L"    - [ESC] (Phim thoat)  : restore and quick exit" << std::endl;
    std::wcout << L"==================================================" << std::endl;

    bool isPaused = false;
    bool running = true;

    while (running) {
        for (int i = 0; i < 10 && running; i++) {
            if (_kbhit()) {
                int ch = _getch();
                if (ch == 27) { // Phim ESC
                    running = false;
                    break;
                } else if (ch == ' ') { // Phim SPACE
                    isPaused = !isPaused;
                    if (isPaused) {
                        ResumeAndResetAll();
                        std::wcout << L"\r[Trang thai: TAM DUNG] (Nhan SPACE de chay tiep)      " << std::flush;
                    } else {
                        std::wcout << L"\r[Trang thai: HOAT DONG]                               " << std::flush;
                    }
                }
            }
            Sleep(200); // 200ms thay vi 100ms: giam 50% CPU overhead cua optimizer
        }

        if (!running) break;

        if (!isPaused) {
            ProcessOptimizationStep();
        }

        size_t suspendedCount = 0;
        if (!isPaused) {
            for (const auto& pair : g_ProcessStates) {
                if (pair.second.isSuspended) suspendedCount++;
            }
        }
        UpdateConsoleTitle(suspendedCount, isPaused);
    }

    ResumeAndResetAll();
    std::wcout << L"\n[+] Da khoi phuc he thong va thoat!" << std::endl;
    return 0;
}
