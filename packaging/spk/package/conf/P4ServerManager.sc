[P4ServerManager]
title="P4 Server Manager"
desc="Web UI and REST API of P4 Server Manager"
port_forward="yes"
dst.ports="@UI_PORT@/tcp"

[P4ServerManager_instances]
title="P4 Servers"
desc="P4 Servers run by P4 Server Manager"
port_forward="yes"
dst.ports="@INSTANCE_PORTS@/tcp"
