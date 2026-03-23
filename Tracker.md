# FiestaLib-Reloaded Tracker

## P1

### Packet read length mismatch detection
If packet read length doesn't match target read length, skip N bytes until they match and show a warning that the reader is wrong. This would catch cases where a struct's Read() method consumes fewer (or more) bytes than the actual payload, preventing desync in stream reading.
