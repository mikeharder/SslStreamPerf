# SslStreamPerf

## Prereqs

### Windows
```
netsh advfirewall firewall add rule name="TCP 8080" dir=in action=allow protocol=TCP localport=8080
```

## Server
```
build.cmd
run-server.cmd
```

## Client
```
build.cmd
run-client.cmd -h host
```
