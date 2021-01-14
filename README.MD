
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
  * This does not refer to the password on the SYSTEM user account, a system password is something else, requiring users to type a password when first connecting. There is no prompt to do so, the user has to know to enter the system password. Once the system password is entered the user is presented with the normal login experience.

* NOT have spaces or special characters in file or folder names, other than $ and _ which are supported.

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

* Disable mail delivery, last login messages and the welcome message for accounbts used for sync:
```
  UAF> MOD <user>/FLAG=(DISMAIL,DISREPORT,DISWELCOME)
```

## Running VMSBuildSync

VMSBuildSync is a .NET Core console application and is started like this:
```
vmsbuildsync <host> <username> <password> <localRootDir> <remoteRootDir> <searchPattern> <forceupdate> <vmsUIC> [<logfile> [<loglevel>]]
```

* `host` is the DNS name or TCP/IP address of the OpenVMS host to connect to.
* `username` is the username of the account to log in to on the OpenVMS system.
* `password` is the password for the user account.
* `localRootDir` is a the Windows path to the root of the directory heirarchy to be synchronized to OpenVMS.
* `remoteRootDir` is the OpenVMS directory specification of the root directory where the windows files and folders are to be synchromized to.
* `searchPatttern is a wildcard pattern describing the files to be copied. Typically * is passed to signify all files.`
* `forceUpdate` ????
* `vmsUIC` is the OpenVMS user identification code that synchronized files should be owned by. Typically this should be the same as the UIC of the user account being used.

### Finding the account UIC

You can display the UIC of the user account by logging in to the account and typing the following command at the DCL prompt:

```
$ write sys$output f$getjpi("","UIC")
```
If the UIC is displayed in a numeric format like `[200,1]` then you are done. If howevewr the UIC is displayed in an alphanumeric format, such as `[GLUSERS]` then yoo will need to run one more command, including the name from the UIC (GLUSERS in this example):

```
$ write sys$output f$fao("!%U",f$identifier("GLUSERS","NAME_TO_NUMBER"))
```
And this command should display the UIC in the numeric form that you need to pass to VMSBuildSync.
