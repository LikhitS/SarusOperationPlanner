"""TCP relay that degrades the link between the ground station (listen port) and SITL (target port).

Modes are read from a control file every 0.5 s, so a test can change them mid-flight:
  ok                  pass everything through
  loss:<pct>          drop <pct>% of data chunks in each direction (corrupts MAVLink framing like a weak radio)
  delay:<ms>          add <ms> one-way latency
  blackout            drop everything (link lost)
Usage: python badlink_proxy.py <listen_port> <target_port> <control_file>
"""
import asyncio
import random
import sys
import time

LISTEN, TARGET, CONTROL = int(sys.argv[1]), int(sys.argv[2]), sys.argv[3]
mode = "ok"


async def watch_control():
    global mode
    last = None
    while True:
        try:
            with open(CONTROL) as f:
                m = f.read().strip() or "ok"
        except OSError:
            m = "ok"
        if m != last:
            print(time.strftime("%H:%M:%S"), "mode", m, flush=True)
            last = m
        mode = m
        await asyncio.sleep(0.5)


async def pump(reader, writer, name):
    try:
        while True:
            data = await reader.read(4096)
            if not data:
                break
            m = mode
            if m == "blackout":
                continue
            if m.startswith("loss:") and random.random() * 100 < float(m[5:]):
                continue
            if m.startswith("delay:"):
                await asyncio.sleep(float(m[6:]) / 1000)
            writer.write(data)
            await writer.drain()
    except (ConnectionError, asyncio.CancelledError):
        pass
    finally:
        writer.close()


async def handle(gcs_reader, gcs_writer):
    sitl_reader, sitl_writer = await asyncio.open_connection("127.0.0.1", TARGET)
    print(time.strftime("%H:%M:%S"), "connected", flush=True)
    await asyncio.gather(pump(gcs_reader, sitl_writer, "up"), pump(sitl_reader, gcs_writer, "down"))


async def main():
    server = await asyncio.start_server(handle, "127.0.0.1", LISTEN)
    asyncio.create_task(watch_control())
    async with server:
        await server.serve_forever()

asyncio.run(main())
