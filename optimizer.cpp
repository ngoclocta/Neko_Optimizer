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
#include <cmath>


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
    bool isRamThrottled; // Ngăn gọi EmptyWorkingSet liên tục
};

std::unordered_set<std::wstring> g_UserWhitelist;
std::unordered_map<DWORD, ProcessState> g_ProcessStates;
DWORD g_SelfPid = 0;

// CPU Core Masks
DWORD_PTR g_ForegroundMask = 3;  // Core 0 + Core 1 (cho Tầng 1)
DWORD_PTR g_BackgroundMask = 2;  // Chỉ chạy trên Core 1 (cho Tầng 2 & Tầng 3)

// Danh sách các tiến trình hệ thống cốt lõi TUYỆT ĐỐI KHÔNG ĐƯỢC CHẠM VÀO (tránh giật lag, màn hình đen)
const std::unordered_set<std::wstring> g_DoNotTouchList = {
    L"idle", L"system", L"smss.exe", L"csrss.exe", L"wininit.exe",
    L"services.exe", L"lsass.exe", L"winlogon.exe", L"svchost.exe",
    L"dwm.exe", L"explorer.exe", L"conhost.exe", L"cmd.exe",
    L"powershell.exe", L"taskmgr.exe", L"spoolsv.exe", L"ctfmon.exe",
    L"runtimebroker.exe", L"audiodg.exe", L"logonui.exe", L"wmiprvse.exe",
    L"fontdrvhost.exe", L"registry", L"memory compression",
    // Các dịch vụ bảo mật & diệt virus
    L"msmpeng.exe", L"nissrv.exe", L"mssense.exe", L"securityhealthservice.exe",
    L"securityhealthsystray.exe", L"smartscreen.exe", L"mpcmdrun.exe"
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

bool EnableDebugPrivilege() {
    HANDLE hToken;
    LUID luid;
    TOKEN_PRIVILEGES tkp;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, &hToken)) {
        return false;
    }
    if (!LookupPrivilegeValue(NULL, SE_DEBUG_NAME, &luid)) {
        CloseHandle(hToken);
        return false;
    }
    tkp.PrivilegeCount = 1;
    tkp.Privileges[0].Luid = luid;
    tkp.Privileges[0].Attributes = SE_PRIVILEGE_ENABLED;
    bool result = AdjustTokenPrivileges(hToken, FALSE, &tkp, sizeof(TOKEN_PRIVILEGES), NULL, NULL);
    CloseHandle(hToken);
    return result && (GetLastError() == ERROR_SUCCESS);
}

bool InitializeSystem() {
    // Kích hoạt đặc quyền Debug để điều phối luồng chính xác
    EnableDebugPrivilege();

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
    return true;
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

// Kiểm tra tiến trình hệ thống bảo vệ (DoNotTouch) hoặc trong thư mục Windows
bool IsCriticalSystemProcess(DWORD pid, const std::wstring& name, const std::wstring& path = L"") {
    if (pid == 0 || pid == 4 || pid == g_SelfPid) return true;
    std::wstring nameLower = ToLower(name);
    if (g_DoNotTouchList.find(nameLower) != g_DoNotTouchList.end()) return true;
    if (!path.empty()) {
        std::wstring pathLower = ToLower(path);
        std::replace(pathLower.begin(), pathLower.end(), L'/', L'\\');
        if (pathLower.rfind(L"c:\\windows\\", 0) == 0 ||
            pathLower.rfind(L"c:\\program files\\windowsapps\\", 0) == 0) {
            return true;
        }
    }
    return false;
}

// Khôi phục tất cả tiến trình về bình thường
void ResumeAndResetAll() {
    if (g_ProcessStates.empty()) return;

    for (auto& pair : g_ProcessStates) {
        ProcessState& state = pair.second;
        HANDLE hProcess = OpenProcess(PROCESS_SET_INFORMATION, FALSE, state.pid);
        if (hProcess) {
            SetPriorityClass(hProcess, NORMAL_PRIORITY_CLASS);
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
    swprintf_s(title, L"[MLFQ Optimizer] Frozen: %zu | %s", 
        suspendedCount, 
        isPaused ? L"PAUSED" : L"ACTIVE"
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
    bool isFgCriticalSystem = IsCriticalSystemProcess(foregroundPid, fgName, fgPath);

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

                // 1. LỌC NHANH THEO TÊN: Tránh gọi OpenProcess/GetProcessPath trên các tiến trình hệ thống để tránh lag
                std::wstring nameLower = ToLower(pe.szExeFile);
                if (g_DoNotTouchList.find(nameLower) != g_DoNotTouchList.end()) {
                    continue;
                }

                // 2. KHỞI TẠO STATE NẾU CHƯA THEO DÕI: Chỉ lấy đường dẫn (GetProcessPath) và check thư mục Windows một lần đầu tiên
                if (g_ProcessStates.find(pe.th32ProcessID) == g_ProcessStates.end()) {
                    std::wstring procPath = GetProcessPath(pe.th32ProcessID);
                    if (IsCriticalSystemProcess(pe.th32ProcessID, pe.szExeFile, procPath)) {
                        continue;
                    }

                    ProcessState newState;
                    newState.pid = pe.th32ProcessID;
                    newState.name = pe.szExeFile;
                    newState.isSuspended = false;
                    newState.isTemporarilyPromoted = false;
                    newState.isRamThrottled = false;
                    
                    if (pe.th32ProcessID == foregroundPid) {
                        newState.tier = TIER_1_REALTIME;
                    } else {
                        newState.tier = TIER_2_NORMAL;
                    }
                    newState.lastStateChangeTime = now;
                    g_ProcessStates[pe.th32ProcessID] = newState;
                }

                ProcessState& state = g_ProcessStates[pe.th32ProcessID];
                bool isWhitelisted = (g_UserWhitelist.find(nameLower) != g_UserWhitelist.end());

                // --- QUẢN LÝ THEO PHÂN CẤP TIER TIẾT KIỆM (KHÔNG DÙNG AFFINITY VÀ SUSPEND ĐỂ TRÁNH GIẬT LAG) ---

                if (pe.th32ProcessID == foregroundPid) {
                    // TẦNG 1: TÁC VỤ ĐANG HOẠT ĐỘNG FOREGROUND -> Đẩy lên HIGH
                    if (state.tier != TIER_1_REALTIME) {
                        HANDLE hProc = OpenProcess(PROCESS_SET_INFORMATION, FALSE, pe.th32ProcessID);
                        if (hProc) {
                            SetPriorityClass(hProc, HIGH_PRIORITY_CLASS);
                            CloseHandle(hProc);
                        }

                        state.tier = TIER_1_REALTIME;
                        state.isRamThrottled = false; // reset để có thể dọn RAM lại khi xuống background
                        state.lastStateChangeTime = now;
                    }
                } 
                else {
                    // CÁC TÁC VỤ CHẠY NỀN (BACKGROUND)

                    if (state.tier == TIER_1_REALTIME) {
                        // Vừa mất tiêu điểm -> Giáng xuống T2
                        state.lastStateChangeTime = now;
                        state.tier = TIER_2_NORMAL;
                        state.isRamThrottled = false;
                    }

                    if (state.tier == TIER_2_NORMAL) {
                        if (isWhitelisted) {
                            // Whitelisted Background (Zalo, Discord, Chrome...): Chạy ở BELOW_NORMAL, dọn RAM đúng 1 lần
                            if (!state.isRamThrottled) {
                                HANDLE hProc = OpenProcess(PROCESS_SET_INFORMATION | PROCESS_SET_QUOTA, FALSE, pe.th32ProcessID);
                                if (hProc) {
                                    SetPriorityClass(hProc, BELOW_NORMAL_PRIORITY_CLASS);
                                    EmptyWorkingSet(hProc);
                                    CloseHandle(hProc);
                                }
                                state.isRamThrottled = true;
                            }
                        } 
                        else {
                            // Bình thường chạy nền: Mới xuống thì chạy ở BELOW_NORMAL
                            if (!state.isRamThrottled) {
                                HANDLE hProc = OpenProcess(PROCESS_SET_INFORMATION, FALSE, pe.th32ProcessID);
                                if (hProc) {
                                    SetPriorityClass(hProc, BELOW_NORMAL_PRIORITY_CLASS);
                                    CloseHandle(hProc);
                                }
                                state.isRamThrottled = true;
                            }

                            // Chờ 30 giây ở Tầng 2 -> Giáng xuống Tầng 3 (IDLE + Dọn RAM)
                            auto duration = std::chrono::duration_cast<std::chrono::seconds>(now - state.lastStateChangeTime).count();
                            if (duration >= 30) {
                                HANDLE hProc = OpenProcess(PROCESS_SET_INFORMATION | PROCESS_SET_QUOTA, FALSE, pe.th32ProcessID);
                                if (hProc) {
                                    SetPriorityClass(hProc, IDLE_PRIORITY_CLASS);
                                    EmptyWorkingSet(hProc);
                                    CloseHandle(hProc);
                                }
                                state.tier = TIER_3_LOW;
                                state.isRamThrottled = true;
                                state.lastStateChangeTime = now;
                            }
                        }
                    } 
                    else if (state.tier == TIER_3_LOW) {
                        // Đã ở Tầng 3 (IDLE). Đảm bảo giữ đúng độ ưu tiên IDLE
                        if (!state.isRamThrottled) {
                            HANDLE hProc = OpenProcess(PROCESS_SET_INFORMATION, FALSE, pe.th32ProcessID);
                            if (hProc) {
                                SetPriorityClass(hProc, IDLE_PRIORITY_CLASS);
                                CloseHandle(hProc);
                            }
                            state.isRamThrottled = true;
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
            std::wcout << L"[-] ERROR: Administrator privilege is required to run this application." << std::endl;
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
        std::wcout << L"[-] ERROR: Cannot initialize system API." << std::endl;
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
    std::wcout << L"[+] Status: Working" << std::endl;
    std::wcout << L"[i] Quick Control Shortcuts:" << std::endl;
    std::wcout << L"    - [SPACE]             : Pause / Resume"        << std::endl;
    std::wcout << L"    - [H]                 : Hide this window to the system tray." << std::endl;
    std::wcout << L"    - [ESC]               : Restore all and exit" << std::endl;
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
                        std::wcout << L"\r[Status: PAUSED] (Press SPACE to resume)             " << std::flush;
                    } else {
                        std::wcout << L"\r[Status: RUNNING]                                    " << std::flush;
                    }
                } else if (ch == 'h' || ch == 'H') { // Phim H
                    HWND hwndConsole = GetConsoleWindow();
                    if (hwndConsole) {
                        ShowWindow(hwndConsole, SW_HIDE);
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
    std::wcout << L"\n[+] System restored and exited successfully!" << std::endl;
    return 0;
}
