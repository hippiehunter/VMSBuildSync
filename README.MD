
# VMSBuildSync

VMSBuildSync is a software development tool that can help OpenVMS developers to configure an environment where they can develop and maintain their source code 
on a Windows system, and have that code mirrored to their OpenVMS environment, in real time, as they work on the code.

The tool implements a file system watcher that monitors a directory tree on Windows. When you first start the tool it synchronizes the files and folders that 
are in the local directory tree on Windows with an identified directory tree on the OpenVMS system. After that, as files and folders change on the Windows system, 
those changes are synchrnized to the OpennVMS system in real time.

## Requirements

The following conditions MUST be met for VMSBuildSync to work:

* Have TCP/IP services 5.7 with ECO 5 installed

* Have SSH server enabled and properly configured

* Have Info-Zip 6.00; earlier versions have caused issues

* Have VMS developer logins that go to the DCL prompt

* Have DCL prompts that end with $

* NOT have secondary passwords enabled

* NOT have a "system password" enabled for developer accounts.
  * This does not refer to the password on the SYSTEM user account, a system password is something else, requiring users to type a password when first connecting. 
  There is no prompt to do so, the user has to know to enter the system password. Once the system password is entered the user is presented with the normal login experience.

* NOT have spaces or special characters in file or folder names, other than $ and _ which are supported.

## Recommendations

To optimize the experience with VMSBuildSync, we recommend the following configuration
guidelines for the OpenVMS developer accounts:

* Minimize any text displayed before login, and between login and the $ prompt being displayed.

* Have a additional developer accounts specifically for file sync that minimize the commands issued during login. Base the name on the developers login, suffixed by "\_",
  or something similar, bearing in mind that you are dealing with a 12-character maximum for usernames!

* In SYLOGIN.COM and LOGIN.COM detect username ending with "\_" and immediately exit. BUT, the process MUST have an UNZIP symbol that points to Info-Zip 6. This can be achieved
  (for accounts ending "\_") by putting this DBL code at the top of each of those files:

```
  $ if f$extract(f$length(f$edit(f$getjpi("","USERNAME"),"COLLAPSE"))-1,1,f$getjpi("","USERNAME")).eqs."_"
  $ then
  $     unzip:==$unzip.exe
  $     exit
  $ endif
```

  This example assumes that unzip.exe is in SYS$SYSTEM: but check and edit appropriately.

* Disable mail delivery, last login messages and the welcome message for accounbts used for sync:
```
  UAF> MOD <user>/FLAG=(DISMAIL,DISREPORT,DISWELCOME)
```

## Running VMSBuildSync

VMSBuildSync is a .NET Core console application and is started like this:
```
  vmsbuildsync <host> <user> <pwd> <localRoot> <remoteRoot> <pattern> <forceUpdate> <uic> [<logFile> [<logLevel>]]
```

* `host` is the DNS name or TCP/IP address of the OpenVMS host to connect to.
* `user` is the username of the account to log in to on the OpenVMS system.
* `password` is the password for the user account.
* `localRoot` is a the Windows path to the root of the directory heirarchy to be synchronized to OpenVMS.
* `remoteRoot` is the OpenVMS device and directory specification of the root directory where the windows files and folders should be synchromized.
* `search` is a wildcard pattern describing the files to be copied. Typically * is passed to signify all files.
* `forceUpdate` ????
* `uic` is the OpenVMS user identification code that synchronized files should be owned by. Typically this should be the same as the UIC of the
   user account being used.
* `logFile` is the name or path of a log file to be created on the windows system. If only a file name is specified then the file will be cretated 
   in the current folder, or a full or relative path may be specified.
* `logLevel` is the logging level to use. Various logging levels are used, including 1, 2, 8 and 10. The higher the logging level the more verbose
   the output. A log level of -1 means log to the console window.

### Finding the account UIC

You can display the UIC of the user account by logging in to the account and typing the following command at the DCL prompt:

```
  $ write sys$output f$getjpi("","UIC")
```
If the UIC is displayed in a numeric format like `[200,1]` then you are done. But if the UIC is displayed in alphanumeric format (e.g. `[GLUSERS]`) 
then you must run another DCL command, including the name from the UIC (GLUSERS in this example):

```
  $ write sys$output f$fao("!%U",f$identifier("GLUSERS","NAME_TO_NUMBER"))
```
And this command should display the UIC in the numeric form that you need to pass to VMSBuildSync.

### Example Command

This is an example command line that micht be used to start VMSBuildSync:

```
vmsbuildsync devaxp jodah_ dbldev C:\MYAPP\SOURCE DKA0:[USER.JODAH.MYAPP.SOURCE] * false [200,1]
```
## Excluding Files and Folders

You can exclude file extensions and even entire folders from the synchronization process by creating a JSON file named `exclusions.json` in the
currentl directory where you run VMSBuildSync from:

```
  {
    "ftypes": [
      ".exe",
      ".txt"
    ],
      "directories": [
      "exe",
      "exludes"
    ]
  }
```

## Problems and Solutions

### Failure to start SFTP Connection

If you see an error like this almost immediately after starting VMSBuildSync:

**ERROR: During SFTP startup an SSH session could not be established! Check SSH is enabed on the server.**

Check the SSH log file `TCPIP$SSH_DEVICE:[TCPIP$SSH]TCPIP$SSH_RUN.LOG`

If you see an error like this:

```
/sys$system/tcpip$ssh_sftp-server2: non-translatable vms error code: 0x2A14
%system-f-exbytlm, exceeded byte count quota
%TCPIP-E-SSH_ERROR, non-specific error condition
```

Then the problem is that the SSH service is exceeding the byte limit quota (BYTLM) on its user account. You should increase the BYTLM quota on the SSH service account,
but by how much. My general guideline is to double the existing value, which you can determine by using the AUTHORIZE utility:

```
UAF> SHOW TCPIP$SSH

Username: TCPIP$SSH                        Owner:  TCPIP$SSH
Account:  TCPIP                            UIC:    [3655,5] ([TCPIP$AUX,TCPIP$SSH])
CLI:      DCL                              Tables: DCLTABLES
Default:  TCPIP$SSH_DEVICE:[TCPIP$SSH]
LGICMD:   LOGIN
Flags:  Restricted
Primary days:   Mon Tue Wed Thu Fri
Secondary days:                     Sat Sun
Primary   000000000011111111112222  Secondary 000000000011111111112222
Day Hours 012345678901234567890123  Day Hours 012345678901234567890123
Network:  ##### Full access ######            ##### Full access ######
Batch:    -----  No access  ------            -----  No access  ------
Local:    -----  No access  ------            -----  No access  ------
Dialup:   -----  No access  ------            -----  No access  ------
Remote:   -----  No access  ------            -----  No access  ------
Expiration:            (none)    Pwdminimum:  6   Login Fails:     0
Pwdlifetime:           (none)    Pwdchange:      (pre-expired)
Last Login:            (none) (interactive), 14-JAN-2021 00:09 (non-interactive)
Maxjobs:         0  Fillm:       100  Bytlm:         32000
Maxacctjobs:     0  Shrfillm:      0  Pbytlm:            0
Maxdetach:       0  BIOlm:       400  JTquota:        4096
Prclm:           8  DIOlm:        50  WSdef:           350
Prio:            8  ASTlm:       100  WSquo:          1024
Queprio:         4  TQElm:        50  WSextent:       4000
CPU:        (none)  Enqlm:       100  Pgflquo:       50000
Authorized Privileges:
  NETMBX       TMPMBX
Default Privileges:
  NETMBX       TMPMBX
```

Look for the numeric value next to `Bytlm`, then set a new value like this:

```
UAF> MOD TCPIP$SSH/BYTLM=64000
```

After doing this you may need to shut down and restart the SSH service. Don't do this if users are connected to the system via SSH! 
And don't do it if YOU are connected to the system via SSH. You may need toget a system admin to restart the SSH service at a suitable
time. To do so:

```
$ @SYS$STARTUP:TCPIP$SSH_SHUTDON.COM
```
Then:
```
$ @SYS$STARTUP:TCPIP$SSH_STARTUP.COM
```
