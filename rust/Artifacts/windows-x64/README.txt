A Windows executable could not be cross-built in the Linux validation container
because the Rust x86_64-pc-windows-gnu standard-library component and MinGW
linker were not installed. Run build-windows.ps1 on Windows; it builds fully
offline from the included source/vendor tree and copies the result here.
