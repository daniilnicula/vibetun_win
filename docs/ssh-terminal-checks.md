# SSH terminal checks

1. Connect to a new test host. Confirm the dialog shows the SSH host-key algorithm and SHA-256 fingerprint. Cancel once and verify the connection is refused; accept on retry and verify the fingerprint is saved.
2. Disconnect and reconnect to the same host/port with the same host key: there should be no second prompt. Change the test host key and reconnect: a warning must show both fingerprints; cancel must refuse the connection.
3. Run `printf '\033[31mred\033[0m white \033[37mwhite ANSI\033[0m \033[38;2;0;180;255mtruecolor\033[0m\n'`. Colored foreground text should use the app accent; default and ANSI-white text should use the terminal's normal foreground.
4. Stream a large output, for example `seq 1 100000`, and confirm typing/scrolling remains responsive. Verify old output is pruned and a single extremely long line is bounded rather than growing indefinitely.
