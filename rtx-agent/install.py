"""Register the agent to start at logon and open its port on Private networks (Windows, as admin).

    python install.py              # install or update
    python install.py --uninstall
    python install.py --start      # also start it now
"""
import argparse, os, subprocess, sys, tempfile

TASK = "LocalCaption RTX Agent"
HERE = os.path.dirname(os.path.abspath(__file__))

# Task Scheduler XML: start at this user's logon, restart every minute on failure, never time out.
TASK_XML = """<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId></LogonTrigger></Triggers>
  <Principals><Principal id="Author"><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType>
    <RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <RestartOnFailure><Interval>PT1M</Interval><Count>999</Count></RestartOnFailure>
    <Enabled>true</Enabled>
  </Settings>
  <Actions Context="Author"><Exec>
    <Command>{pythonw}</Command><Arguments>"{agent}" --port {port}{extra}</Arguments>
    <WorkingDirectory>{here}</WorkingDirectory>
  </Exec></Actions>
</Task>
"""


def run(*cmd, check=True):
    print(">", " ".join(cmd))
    return subprocess.run(cmd, check=check)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--uninstall", action="store_true")
    ap.add_argument("--start", action="store_true")
    ap.add_argument("--port", type=int, default=8765)
    ap.add_argument("--no-pairing", action="store_true",
                    help="the agent accepts any device on this network without a pairing code")
    a = ap.parse_args()
    if sys.platform != "win32":
        sys.exit("install.py is for the Windows RTX desktop")

    run("schtasks", "/Delete", "/TN", TASK, "/F", check=False)
    run("netsh", "advfirewall", "firewall", "delete", "rule", f"name={TASK}", check=False)
    if a.uninstall:
        return

    # pythonw: no console window. Use the venv this script runs from, so packages match.
    pythonw = os.path.join(os.path.dirname(sys.executable), "pythonw.exe")
    # whoami, not USERDOMAIN: over SSH the latter is "WORKGROUP", which Task Scheduler rejects.
    user = subprocess.run(["whoami"], capture_output=True, text=True, check=True).stdout.strip()
    xml = TASK_XML.format(user=user,
                          pythonw=pythonw, agent=os.path.join(HERE, "agent.py"), here=HERE, port=a.port,
                          extra=" --no-pairing" if a.no_pairing else "")
    with tempfile.NamedTemporaryFile("w", suffix=".xml", delete=False, encoding="utf-16") as f:
        f.write(xml)
    try:
        run("schtasks", "/Create", "/TN", TASK, "/XML", f.name, "/F")
    finally:
        os.unlink(f.name)
    # Private profile only: the agent must not be reachable on public Wi-Fi.
    run("netsh", "advfirewall", "firewall", "add", "rule", f"name={TASK}", "dir=in", "action=allow",
        "protocol=TCP", f"localport={a.port}", "profile=private")
    if a.start:
        run("schtasks", "/Run", "/TN", TASK)
    if a.no_pairing:
        print("\nInstalled. The agent starts at logon. Pairing is off: any device on this network can use it.")
    else:
        print(f"\nInstalled. The agent starts at logon; its pairing code is in {HERE}\\state\\pairing-code.txt")


if __name__ == "__main__":
    main()
