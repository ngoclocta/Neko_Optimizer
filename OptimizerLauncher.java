import javax.swing.*;
import javax.swing.border.EmptyBorder;
import java.awt.*;
import java.awt.event.*;
import java.awt.image.BufferedImage;
import java.io.*;
import java.nio.file.Files;
import java.nio.file.Paths;
import java.util.ArrayList;
import java.util.List;

public class OptimizerLauncher {
    private static JFrame frame;
    private static JTextArea logArea;
    private static JTextArea whitelistArea;
    private static JLabel statusLabel;
    private static JButton toggleButton;
    private static Process optimizerProcess = null;
    private static boolean isPaused = false;
    private static SystemTray tray;
    private static TrayIcon trayIcon;

    // Sleek Dark Theme Colors
    private static final Color BG_DARK = new Color(26, 29, 32);
    private static final Color BG_CARD = new Color(33, 37, 41);
    private static final Color TEXT_LIGHT = new Color(248, 249, 250);
    private static final Color TEXT_MUTED = new Color(173, 181, 189);
    private static final Color ACCENT_GREEN = new Color(46, 204, 113);
    private static final Color ACCENT_YELLOW = new Color(241, 196, 15);
    private static final Color ACCENT_RED = new Color(231, 76, 60);
    private static final Color BUTTON_BG = new Color(52, 58, 64);
    private static final Color BUTTON_HOVER = new Color(73, 80, 87);

    public static void main(String[] args) {
        // Step 1: Check Admin Privileges
        if (!isAdmin()) {
            elevateAndRestart();
            System.exit(0);
        }

        // Step 2: Initialize UI
        try {
            UIManager.setLookAndFeel(UIManager.getSystemLookAndFeelClassName());
        } catch (Exception ignored) {}

        SwingUtilities.invokeLater(OptimizerLauncher::createAndShowGUI);
    }

    private static boolean isAdmin() {
        try {
            // Reg query on HKU\S-1-5-19 requires admin privileges
            Process p = Runtime.getRuntime().exec("reg query HKU\\S-1-5-19");
            return p.waitFor() == 0;
        } catch (Exception e) {
            return false;
        }
    }

    private static void elevateAndRestart() {
        try {
            String javaHome = System.getProperty("java.home");
            String javaBin = javaHome + File.separator + "bin" + File.separator + "java";
            java.net.URI uri = OptimizerLauncher.class.getProtectionDomain().getCodeSource().getLocation().toURI();
            String jarPath = new File(uri).getAbsolutePath();
            
            // If running as class file directly (not jar)
            String cmd;
            if (jarPath.endsWith(".jar")) {
                cmd = "\"" + javaBin + "\" -jar \"" + jarPath + "\"";
            } else {
                String classPath = System.getProperty("java.class.path");
                cmd = "\"" + javaBin + "\" -cp \"" + classPath + "\" OptimizerLauncher";
            }

            // Execute via PowerShell runas verb
            String psCmd = "Start-Process cmd.exe -ArgumentList '/c " + cmd + "' -Verb RunAs -WindowStyle Hidden";
            Runtime.getRuntime().exec(new String[]{"powershell", "-Command", psCmd});
        } catch (Exception e) {
            JOptionPane.showMessageDialog(null, "Cannot elevate privileges. Please run the application as Administrator.", "Privilege Error", JOptionPane.ERROR_MESSAGE);
        }
    }

    private static void createAndShowGUI() {
        frame = new JFrame("Neko CPU Optimizer Launcher");
        frame.setDefaultCloseOperation(JFrame.DO_NOTHING_ON_CLOSE);
        frame.setSize(850, 600);
        frame.getContentPane().setBackground(BG_DARK);
        frame.setLayout(new BorderLayout());

        // Custom Title Bar Icon (if exists, else default)
        try {
            Image img = Toolkit.getDefaultToolkit().getImage("image.png");
            if (img != null) frame.setIconImage(img);
        } catch (Exception ignored) {}

        // Add Window Listener for closing to tray
        frame.addWindowListener(new WindowAdapter() {
            @Override
            public void windowClosing(WindowEvent e) {
                minimizeToTray();
            }
        });

        // Left Panel - Control & Whitelist Editor
        JPanel leftPanel = new JPanel();
        leftPanel.setBackground(BG_DARK);
        leftPanel.setPreferredSize(new Dimension(350, 600));
        leftPanel.setLayout(new BorderLayout(10, 10));
        leftPanel.setBorder(new EmptyBorder(15, 15, 15, 10));

        // Control Card
        JPanel controlCard = new JPanel();
        controlCard.setBackground(BG_CARD);
        controlCard.setLayout(new GridLayout(4, 1, 10, 10));
        controlCard.setBorder(new EmptyBorder(15, 15, 15, 15));

        JLabel titleLabel = new JLabel("Neko Cpu Optimizer v1.1", JLabel.CENTER);
        titleLabel.setFont(new Font("Segoe UI", Font.BOLD, 18));
        titleLabel.setForeground(TEXT_LIGHT);
        controlCard.add(titleLabel);

        statusLabel = new JLabel("STATUS: STOPPED", JLabel.CENTER);
        statusLabel.setFont(new Font("Segoe UI", Font.BOLD, 14));
        statusLabel.setForeground(ACCENT_RED);
        controlCard.add(statusLabel);

        toggleButton = createStyledButton("Start Optimizer", BUTTON_BG);
        toggleButton.addActionListener(e -> toggleOptimizer());
        controlCard.add(toggleButton);

        JButton hideButton = createStyledButton("Hide to Tray", BUTTON_BG);
        hideButton.addActionListener(e -> minimizeToTray());
        controlCard.add(hideButton);

        leftPanel.add(controlCard, BorderLayout.NORTH);

        // Whitelist Editor Card
        JPanel whitelistCard = new JPanel();
        whitelistCard.setBackground(BG_CARD);
        whitelistCard.setLayout(new BorderLayout(5, 5));
        whitelistCard.setBorder(new EmptyBorder(15, 15, 15, 15));

        JLabel wlTitle = new JLabel("Whitelist (whitelist.txt)");
        wlTitle.setFont(new Font("Segoe UI", Font.BOLD, 13));
        wlTitle.setForeground(TEXT_LIGHT);
        whitelistCard.add(wlTitle, BorderLayout.NORTH);

        whitelistArea = new JTextArea();
        whitelistArea.setBackground(BG_DARK);
        whitelistArea.setForeground(TEXT_LIGHT);
        whitelistArea.setCaretColor(TEXT_LIGHT);
        whitelistArea.setFont(new Font("Consolas", Font.PLAIN, 12));
        whitelistArea.setBorder(BorderFactory.createLineBorder(BUTTON_BG, 1));
        
        loadWhitelistFile();

        JScrollPane wlScroll = new JScrollPane(whitelistArea);
        wlScroll.setBorder(null);
        whitelistCard.add(wlScroll, BorderLayout.CENTER);

        JButton saveWlButton = createStyledButton("Save & Reload Whitelist", BUTTON_BG);
        saveWlButton.addActionListener(e -> saveWhitelistFile());
        whitelistCard.add(saveWlButton, BorderLayout.SOUTH);

        leftPanel.add(whitelistCard, BorderLayout.CENTER);

        // Right Panel - Console Output Log
        JPanel rightPanel = new JPanel();
        rightPanel.setBackground(BG_DARK);
        rightPanel.setLayout(new BorderLayout());
        rightPanel.setBorder(new EmptyBorder(15, 10, 15, 15));

        JPanel logCard = new JPanel();
        logCard.setBackground(BG_CARD);
        logCard.setLayout(new BorderLayout());
        logCard.setBorder(new EmptyBorder(15, 15, 15, 15));

        JLabel logTitle = new JLabel("Activity Log (Console Output)");
        logTitle.setFont(new Font("Segoe UI", Font.BOLD, 13));
        logTitle.setForeground(TEXT_LIGHT);
        logTitle.setBorder(new EmptyBorder(0, 0, 10, 0));
        logCard.add(logTitle, BorderLayout.NORTH);

        logArea = new JTextArea();
        logArea.setEditable(false);
        logArea.setBackground(BG_DARK);
        logArea.setForeground(ACCENT_GREEN);
        logArea.setFont(new Font("Consolas", Font.PLAIN, 12));
        logArea.setLineWrap(true);
        logArea.setBorder(BorderFactory.createLineBorder(BUTTON_BG, 1));

        JScrollPane logScroll = new JScrollPane(logArea);
        logScroll.setBorder(null);
        logCard.add(logScroll, BorderLayout.CENTER);

        rightPanel.add(logCard, BorderLayout.CENTER);

        frame.add(leftPanel, BorderLayout.WEST);
        frame.add(rightPanel, BorderLayout.CENTER);

        // Setup System Tray
        setupSystemTray();

        frame.setLocationRelativeTo(null);
        frame.setVisible(true);

        // Auto start optimizer on open
        startOptimizerProcess();
    }

    private static JButton createStyledButton(String text, Color bg) {
        JButton btn = new JButton(text);
        btn.setFont(new Font("Segoe UI", Font.BOLD, 12));
        btn.setForeground(TEXT_LIGHT);
        btn.setBackground(bg);
        btn.setFocusPainted(false);
        btn.setBorderPainted(false);
        btn.setCursor(new Cursor(Cursor.HAND_CURSOR));

        btn.addMouseListener(new MouseAdapter() {
            @Override
            public void mouseEntered(MouseEvent e) {
                btn.setBackground(BUTTON_HOVER);
            }
            @Override
            public void mouseExited(MouseEvent e) {
                btn.setBackground(bg);
            }
        });
        return btn;
    }

    private static void log(String message) {
        SwingUtilities.invokeLater(() -> {
            logArea.append(message + "\n");
            logArea.setCaretPosition(logArea.getDocument().getLength());
        });
    }

    private static void toggleOptimizer() {
        if (optimizerProcess == null) {
            startOptimizerProcess();
        } else {
            stopOptimizerProcess();
        }
    }

    private static void startOptimizerProcess() {
        if (optimizerProcess != null) return;
        
        File exeFile = new File("neko_optimizer.exe");
        if (!exeFile.exists()) {
            log("Error: neko_optimizer.exe not found. Please build the C++ core first.");
            statusLabel.setText("ERROR: MISSING OPTIMIZER.EXE");
            statusLabel.setForeground(ACCENT_RED);
            return;
        }

        log("Starting C++ optimizer core...");
        try {
            ProcessBuilder pb = new ProcessBuilder("neko_optimizer.exe");
            pb.redirectErrorStream(true);
            optimizerProcess = pb.start();

            // Thread to read output
            new Thread(() -> {
                try (BufferedReader reader = new BufferedReader(new InputStreamReader(optimizerProcess.getInputStream(), "UTF-8"))) {
                    String line;
                    while ((line = reader.readLine()) != null) {
                        log(line);
                    }
                } catch (Exception e) {
                    log("Disconnected from C++ core: " + e.getMessage());
                }
                handleProcessExit();
            }).start();

            statusLabel.setText("STATUS: RUNNING");
            statusLabel.setForeground(ACCENT_GREEN);
            toggleButton.setText("Stop Optimizer");
        } catch (Exception e) {
            log("Failed to start optimizer.exe: " + e.getMessage());
            statusLabel.setText("STARTUP FAILED");
            statusLabel.setForeground(ACCENT_RED);
        }
    }

    private static void stopOptimizerProcess() {
        if (optimizerProcess == null) return;
        log("Stopping C++ optimizer core...");
        
        // Send ESC code to close gracefully
        try {
            // Write ESC or close process
            optimizerProcess.destroy();
            optimizerProcess.waitFor();
        } catch (Exception ignored) {}
        
        optimizerProcess = null;
        statusLabel.setText("STATUS: STOPPED");
        statusLabel.setForeground(ACCENT_RED);
        toggleButton.setText("Start Optimizer");
        log("Stopped successfully. Restored system resource limits.");
    }

    private static void handleProcessExit() {
        optimizerProcess = null;
        SwingUtilities.invokeLater(() -> {
            statusLabel.setText("STATUS: STOPPED");
            statusLabel.setForeground(ACCENT_RED);
            toggleButton.setText("Start Optimizer");
        });
    }

    private static void loadWhitelistFile() {
        File file = new File("whitelist.txt");
        if (!file.exists()) {
            whitelistArea.setText("chrome.exe\ndiscord.exe\nspotify.exe\nzalo.exe");
            return;
        }
        try {
            List<String> lines = Files.readAllLines(Paths.get("whitelist.txt"));
            whitelistArea.setText(String.join("\n", lines));
        } catch (Exception e) {
            log("Error loading whitelist: " + e.getMessage());
        }
    }

    private static void saveWhitelistFile() {
        try {
            Files.write(Paths.get("whitelist.txt"), whitelistArea.getText().getBytes());
            log("Saved whitelist.txt successfully.");
            // Restart core to apply changes
            if (optimizerProcess != null) {
                log("Restarting core to apply new whitelist...");
                stopOptimizerProcess();
                startOptimizerProcess();
            }
        } catch (Exception e) {
            log("Error saving whitelist: " + e.getMessage());
            JOptionPane.showMessageDialog(frame, "Cannot save whitelist.txt: " + e.getMessage(), "Error", JOptionPane.ERROR_MESSAGE);
        }
    }

    private static void setupSystemTray() {
        if (!SystemTray.isSupported()) return;

        tray = SystemTray.getSystemTray();
        Image image = null;
        try {
            image = Toolkit.getDefaultToolkit().getImage("image.png");
        } catch (Exception ignored) {}

        if (image == null) {
            // Default blank icon
            image = new BufferedImage(16, 16, BufferedImage.TYPE_INT_ARGB);
        }

        PopupMenu popup = new PopupMenu();
        
        MenuItem showItem = new MenuItem("Show GUI");
        showItem.addActionListener(e -> {
            frame.setVisible(true);
            frame.setExtendedState(JFrame.NORMAL);
        });
        popup.add(showItem);

        MenuItem stopItem = new MenuItem("Start / Stop");
        stopItem.addActionListener(e -> toggleOptimizer());
        popup.add(stopItem);

        popup.addSeparator();

        MenuItem exitItem = new MenuItem("Exit");
        exitItem.addActionListener(e -> {
            stopOptimizerProcess();
            System.exit(0);
        });
        popup.add(exitItem);

        trayIcon = new TrayIcon(image, "Neko CPU Optimizer", popup);
        trayIcon.setImageAutoSize(true);
        trayIcon.addActionListener(e -> {
            frame.setVisible(true);
            frame.setExtendedState(JFrame.NORMAL);
        });

        try {
            tray.add(trayIcon);
        } catch (Exception ignored) {}
    }

    private static void minimizeToTray() {
        if (SystemTray.isSupported()) {
            frame.setVisible(false);
            trayIcon.displayMessage("Neko CPU Optimizer", "The application is running in the background.", TrayIcon.MessageType.INFO);
        } else {
            System.exit(0);
        }
    }
}
