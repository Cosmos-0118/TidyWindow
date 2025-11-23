# PathPilot Runtime Switchboard Configuration

## 🌟 A. TRUE SWITCHABLE RUNTIMES (24 items)

These are the primary programming languages and runtimes developers commonly install multiple versions of on Windows. They fully support safe version switching via PATH or environment changes.

| ID        | Display Name | Executable Name    | Version Arguments   | Description                                                 |
|-----------|--------------|--------------------|--------------------|-------------------------------------------------------------|
| python    | Python       | python.exe         | ["--version"]      | CPython distributions including python.org, Microsoft Store |
| node      | Node.js      | node.exe           | ["--version"]      | Node.js runtime and related distributions                   |
| jdk       | Java (JDK)   | java.exe           | ["-version"]       | Oracle, OpenJDK, Temurin, Zulu, and other JDK distributions  |
| dotnet    | .NET SDK     | dotnet.exe         | ["--version"]      | .NET Core and .NET SDK CLI                                  |
| go        | Go           | go.exe             | ["version"]        | Go language compiler and tools                              |
| ruby      | Ruby         | ruby.exe           | ["--version"]      | Ruby interpreter                                            |
| php       | PHP          | php.exe            | ["--version"]      | PHP runtime                                                |
| perl      | Perl         | perl.exe           | ["--version"]      | Perl interpreter                                           |
| rust      | Rust         | rustc.exe          | ["--version"]      | Rust compiler                                             |
| scala     | Scala        | scala.exe          | ["-version"]       | Scala runtime and compiler                                 |
| kotlin    | Kotlin       | kotlinc.exe        | ["-version"]       | Kotlin compiler                                           |
| swift     | Swift        | swift.exe          | ["--version"]      | Swift programming language compiler                        |
| ghc       | Haskell (GHC)| ghc.exe            | ["--version"]      | Glasgow Haskell Compiler                                   |
| lua       | Lua          | lua.exe            | ["-v"]             | Lua interpreter                                           |
| luajit    | LuaJIT       | luajit.exe         | ["-v"]             | Just-In-Time Lua compiler                                 |
| julia     | Julia        | julia.exe          | ["--version"]      | Julia programming language                                |
| deno      | Deno         | deno.exe           | ["--version"]      | Modern JavaScript/TypeScript runtime                      |
| dart      | Dart         | dart.exe           | ["--version"]      | Dart SDK                                                 |
| elixir    | Elixir       | elixir.bat         | ["--version"]      | Elixir language runtime                                   |
| erl       | Erlang       | erl.exe            | ["-version"]       | Erlang runtime                                           |
| ocaml     | OCaml        | ocamlc.exe         | ["-version"]       | OCaml compiler                                          |
| nim       | Nim          | nim.exe            | ["--version"]      | Nim programming language compiler                      |
| crystal   | Crystal      | crystal.exe        | ["--version"]      | Crystal language compiler                               |
| clojure   | Clojure      | clojure.bat        | ["--version"]      | JVM based Lisp dialect                                  |

---

## 🌙 B. LESS COMMON BUT STILL REAL RUNTIMES (12 items)

These are niche but legitimate languages. They typically support version switching but it may be less common or optional.

| ID        | Display Name        | Executable Name    | Version Arguments   | Description                                       |
|-----------|---------------------|--------------------|--------------------|-------------------------------------------------|
| fsharp    | F#                  | fsc.exe            | ["--version"]      | F# compiler (dotnet based)                       |
| groovy    | Groovy              | groovy.bat         | ["--version"]      | JVM based scripting language                     |
| r         | R                   | R.exe              | ["--version"]      | Statistical computing language                   |
| racket    | Scheme (Racket)     | racket.exe         | ["--version"]      | Scheme language implementation                   |
| sbcl      | Common Lisp (SBCL)  | sbcl.exe           | ["--version"]      | Steel Bank Common Lisp implementation             |
| swi-prolog| Prolog (SWI-Prolog) | swipl.exe          | ["--version"]      | Prolog language runtime                          |
| gfortran  | Fortran (gfortran)  | gfortran.exe       | ["--version"]      | GNU Fortran compiler                             |
| tcl       | Tcl                 | tclsh.exe          | ["--version"]      | Tool Command Language runtime                    |
| vala      | Vala                | valac.exe          | ["--version"]      | Vala language compiler                           |
| zig       | Zig                 | zig.exe            | ["version"]        | Zig programming language                         |
| haxe      | Haxe                | haxe.exe           | ["--version"]      | Multi-platform language                          |
| reasonml  | ReasonML / ReScript | refmt.exe          | ["--version"]      | Reason compiler for OCaml and JS interoperability |

---

### Notes:
- `versionArguments` indicate the command-line parameters to query the version of the executable.
- Executable names are commonly found binaries to run on Windows.
- JVM-based languages like Clojure and Groovy typically ship with `.bat` launchers that wrap Java.
- Some runtimes may output version info to stderr (e.g., `java -version`), so your probe logic should handle that.

---

This structured list aligns perfectly with PathPilot’s goal of managing real, active, version-switchable runtimes across developer machines.