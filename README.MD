
# VMSBuildSync

VMSBuildSync is a software development tool that can help OpenVMS developers to configure an environment where they can develop and maintain their source code on a Windows system, and have that code mirrored to their OpenVMS environment, in real time, as they work on the code.

The tool implements a file system watcher that monitors a directory tree on Windows. When you first start the tool it synchronizes the files and folders that are in the local directory tree on Windows with an identified directory tree on the OpenVMS system. After that, as files and folders change on the Windows system, those changes are synchrnized to the OpennVMS system in real time.

## Requirements

The following conditions MUST be met for VMSBuildSync to work:

* Have TCP/IP services 5.7 with ECO 5 installed

* Have SSH server enabled and properly configured

* Have Info-Zip 6.00; earlier versions have caused issues

* Have VMS developer logins that go to the DCL prompt

* Have DCL prompts that end with $

* NOT have secondary passwords enabled

* NOT have a "system password" enabled for developer accounts.
  * This does not refer to the password on the SYSTEM user account, a system password is
    something else, requiring users to type a password when first connecting, there is no
	prompt. They have to know to enter the system password to then be subsequently
	prompted for a username.

## Recommendations

To optimize the experience with VMSBuildSync, we recommend the following configuration
guidelines for the OpenVMS developer accounts:

* Minimize any text displayed before login, and between login and the $ prompt being displayed.

* Have a additional developer accounts specifically for file sync that minimize the
  commands issued during login. Base the name on the developers login, suffixed by "\_",
  or something similar, bearing in mind that you are dealing with a 12-character maximum
  for usernames!

* In SYLOGIN.COM and LOGIN.COM detect username ending with "\_" and immediately exit.
  BUT, the process MUST have an UNZIP symbol that points to Info-Zip 6. This can be achieved
  (for accounts ending "\_") by putting this DBL code at the top of each of those files:

```
  $ if f$extract(f$length(f$edit(f$getjpi("","USERNAME"),"COLLAPSE"))-1,1,f$getjpi("","USERNAME")).eqs."_"
  $ then
  $     unzip:==$unzip.exe
  $     exit
  $ endif
```

  This example assumes that unzip.exe is in SYS$SYSTEM: but check and edit appropriately.

* Disable mail delivery to the account:
```
  UAF> MOD <user>/FLAG=DISMAIL
```

* Suppresses reports of the last login time, login failures, and other security reports:
```
  UAF> MOD <user>/FLAG=DISREPORT
```

* Suppresses the welcome message:
```
  UAF> MOD <user>/FLAG=DISWELCOME
```
