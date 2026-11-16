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

# The profiles are copied to the shared folder at each start of the package: the web interface offers to import them.
legacy_note=""
if [ -n "$profile_list" ]; then
    legacy_note="<br><br>This NAS has server profiles of the <i>Perforce P4 Server (p4d)</i> package (HelixCoreServer):<ul>$profile_list</ul>The Instances page of the web interface will list them, and imports only the ones you choose. Stop the HelixCoreServer package before you start an imported server on the same port."
fi

PKG_NAME="${SYNOPKG_PKGNAME:-P4ServerManager}"
SHARE_NAME="P4ServerManager"

# Data folders a previous installation may have left: DSM keeps the shared folder when the package is uninstalled with
# its data. Its path was saved in etc/data-dir (see scripts/common), which DSM keeps under @appconf; the shared folder
# is also looked for at its default place.
previous_data_dirs() {
    for file in "/var/packages/$PKG_NAME/etc/data-dir" /volume*/@appconf/"$PKG_NAME"/data-dir; do
        [ -r "$file" ] && cat "$file"
    done
    for dir in /volume*/"$SHARE_NAME"; do
        echo "$dir"
    done
}

# True when one of those folders holds accounts. The daemon keeps the has-accounts file next to its database while the
# database has accounts (DaemonPaths.AccountsMarkerPath), so that the wizard needs not know the schema. When it cannot
# tell, the wizard asks for the account as on a first installation; the daemon ignores it if accounts exist.
previous_accounts_exist() {
    previous_data_dirs | while IFS= read -r dir; do
        if [ -f "$dir/has-accounts" ] && [ -f "$dir/p4servermanager.db" ]; then
            echo found
            break
        fi
    done | grep -q found
}

account_note=""
if previous_accounts_exist; then
    account_note="<br><br>The P4ServerManager shared folder holds the accounts of a previous installation: sign in with one of them. A lost password is reset over SSH with <i>sudo p4sm user set-password &lt;name&gt;</i>."
fi

cat >"$SYNOPKG_TEMP_LOGFILE" <<EOF
[
    {
        "step_title": "P4 Server Manager",
        "items": [
            {
                "type": "singleselect",
                "desc": "P4 Server Manager is not affiliated with Perforce Software, nor is it an official P4 tool. It downloads the p4d builds you choose from Perforce.<br><br>The P4ServerManager shared folder will hold its database, the p4d builds and the P4 servers you create in their default location.<br><br>The web interface listens on port @UI_PORT@ and asks to sign in. Keep that port closed to untrusted networks, or reach it through HTTPS (Control Panel > Login Portal > Reverse Proxy).$account_note$legacy_note",
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

# The first account of the web interface, unless a previous installation left accounts: the daemon would ignore it.
# Left empty, the web interface asks for it on the first visit. The account is created by the daemon at its first
# start; preinst checks that both passwords match.
if [ -z "$account_note" ]; then
    cat >>"$SYNOPKG_TEMP_LOGFILE" <<'EOF'
    ,
    {
        "step_title": "Administrator account",
        "items": [
            {
                "type": "textfield",
                "desc": "The account that signs in to the web interface. It can add other accounts. Leave the password empty to create the account from the web interface on the first visit: until then, anybody who reaches the port can create it.",
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
fi

echo "]" >>"$SYNOPKG_TEMP_LOGFILE"
exit 0
