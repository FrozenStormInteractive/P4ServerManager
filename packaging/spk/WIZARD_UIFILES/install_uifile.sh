#!/bin/sh
# Install wizard. Package Center runs it before the installation and shows the JSON it writes to
# $SYNOPKG_TEMP_LOGFILE. The package scripts are not installed yet: the detection of the profiles of the
# HelixCoreServer package is a copy of the one in scripts/common, keep both in sync.

LEGACY_PACKAGE="HelixCoreServer"

# Folders that may hold p4dctl-ng profiles: the default one next to the configuration of the package, the folders
# its configurations include, and the same under @appconf, which DSM keeps after the package is uninstalled.
legacy_profile_dirs() {
    for etc_dir in "/var/packages/$LEGACY_PACKAGE/etc" /volume*/@appconf/"$LEGACY_PACKAGE"; do
        [ -d "$etc_dir" ] || continue
        echo "$etc_dir/p4dctl-ng.conf.d"
        legacy_includes "$etc_dir/p4dctl-ng.conf"
    done
    legacy_includes /etc/perforce/p4dctlng.conf
}

legacy_includes() {
    [ -r "$1" ] && command -v jq >/dev/null 2>&1 || return 0
    jq -r '.Includes[]? // empty' "$1" 2>/dev/null
}

# One line per profile file, by real path so that a folder reached through a symbolic link is only listed once.
legacy_profile_files() {
    legacy_profile_dirs | while IFS= read -r dir; do
        [ -d "$dir" ] || continue
        for file in "$dir"/*; do
            [ -f "$file" ] && [ -r "$file" ] && grep -q 'P4ROOT' "$file" && readlink -f "$file"
        done
    done | sort -u
}

# File names only, reduced to characters that need no escaping in JSON or HTML.
profile_list=$(legacy_profile_files | while IFS= read -r file; do
    printf '<li>%s</li>' "$(basename "$file" | tr -cd 'A-Za-z0-9._-')"
done)

# The profiles are copied to the data of the package at each start of the package: the web interface offers to import
# them.
legacy_note=""
if [ -n "$profile_list" ]; then
    legacy_note="<br><br>This NAS has server profiles of the <i>Perforce P4 Server (p4d)</i> package (HelixCoreServer):<ul>$profile_list</ul>The Instances page of the web interface will list them, and imports only the ones you choose. Stop the HelixCoreServer package before you start an imported server on the same port."
fi

cat >"$SYNOPKG_TEMP_LOGFILE" <<EOF
[
    {
        "step_title": "P4 Server Manager",
        "items": [
            {
                "type": "singleselect",
                "desc": "P4 Server Manager is not affiliated with Perforce Software, nor is it an official P4 tool. It downloads the p4d builds you choose from Perforce.<br><br>Its database and the p4d builds stay in the folder of the package. The P4ServerManager shared folder will hold the P4 servers you create in their default location, one folder per server, named after it.<br><br>The web interface listens on port @UI_PORT@ and asks to sign in. Keep that port closed to untrusted networks, or reach it through HTTPS (Control Panel > Login Portal > Reverse Proxy).$legacy_note",
                "subitems": [
                    {
                        "key": "wizard_understood",
                        "desc": "I understand",
                        "defaultValue": true
                    }
                ]
            }
        ]
    }
EOF

# The first account of the web interface. Left empty, the web interface asks for it on the first visit. The account is
# created by the daemon at its first start, and ignored when the database already has accounts; preinst checks that
# both passwords match. The step is always shown: the wizard cannot tell whether a previous installation left accounts,
# since Package Center does not tell it the volume the package is installed to (SYNOPKG_PKGDEST_VOL is not set there),
# and skipping the step wrongly would leave the first account to whoever reaches the port.
cat >>"$SYNOPKG_TEMP_LOGFILE" <<'EOF'
    ,
    {
        "step_title": "Administrator account",
        "items": [
            {
                "type": "textfield",
                "desc": "The account that signs in to the web interface. It can add other accounts. Leave the password empty to create the account from the web interface on the first visit: until then, anybody who reaches the port can create it.<br><br>If a previous installation left accounts on this volume, they are kept: sign in with one of them, and this account is ignored. A lost password is reset over SSH with <i>sudo p4sm user set-password &lt;name&gt;</i>.",
                "subitems": [
                    {
                        "key": "wizard_admin_user",
                        "desc": "User name",
                        "defaultValue": "admin",
                        "validator": {
                            "allowBlank": false,
                            "regex": {
                                "expr": "/^[A-Za-z0-9][A-Za-z0-9._@-]{0,63}$/",
                                "errorText": "A letter or a digit, then up to 63 letters, digits, '.', '-', '_' or '@'."
                            }
                        }
                    }
                ]
            },
            {
                "type": "password",
                "subitems": [
                    {
                        "key": "wizard_admin_password",
                        "desc": "Password",
                        "validator": {
                            "allowBlank": true,
                            "regex": {
                                "expr": "/^.{10,128}$/",
                                "errorText": "Between 10 and 128 characters."
                            }
                        }
                    },
                    {
                        "key": "wizard_admin_password_confirm",
                        "desc": "Confirm the password",
                        "validator": {
                            "allowBlank": true
                        }
                    }
                ]
            }
        ]
    }
EOF

echo "]" >>"$SYNOPKG_TEMP_LOGFILE"
exit 0
