var SandboxWebSocketPlugin = {
    $sandboxWsState: {
        socket: null,
        events: [],
        viewerId: null,
        pasteBound: false,
        guardsBound: false
    },

    SandboxWsGetOrCreateViewerId: function () {
        if (!sandboxWsState.pasteBound && typeof window !== "undefined" && window.addEventListener) {
            sandboxWsState.pasteBound = true;
            window.addEventListener("paste", function (ev) {
                var clip = ev && ev.clipboardData ? ev.clipboardData.getData("text") : "";
                if (clip) {
                    sandboxWsState.events.push("PASTE:" + clip);
                }
            });
        }
        if (!sandboxWsState.viewerId) {
            var storageKey = "mc_remote_unity_viewer_id_v1";
            var existing = null;
            try {
                existing = window.localStorage.getItem(storageKey);
            } catch (e) {
                existing = null;
            }
            if (existing && existing.length >= 16) {
                sandboxWsState.viewerId = existing;
            } else {
                var bytes = new Uint8Array(16);
                if (window.crypto && window.crypto.getRandomValues) {
                    window.crypto.getRandomValues(bytes);
                } else {
                    for (var i = 0; i < bytes.length; i++) {
                        bytes[i] = Math.floor(Math.random() * 256);
                    }
                }
                var hex = "";
                for (var j = 0; j < bytes.length; j++) {
                    var b = bytes[j].toString(16);
                    hex += (b.length === 1 ? "0" + b : b);
                }
                sandboxWsState.viewerId = "v_" + hex;
                try {
                    window.localStorage.setItem(storageKey, sandboxWsState.viewerId);
                } catch (e) {
                }
            }
        }
        var len = lengthBytesUTF8(sandboxWsState.viewerId) + 1;
        var ptr = _malloc(len);
        stringToUTF8(sandboxWsState.viewerId, ptr, len);
        return ptr;
    },

    SandboxWsGetDefaultUrl: function () {
        var target = "";
        try {
            var params = new URLSearchParams(window.location.search || "");
            var overrideWs = params.get("ws");
            if (overrideWs) {
                target = overrideWs;
            } else if (window.location && window.location.host) {
                var proto = window.location.protocol === "https:" ? "wss://" : "ws://";
                target = proto + window.location.host + "/ws/sync";
            }
        } catch (e) {
            target = "";
        }
        var len = lengthBytesUTF8(target) + 1;
        var ptr = _malloc(len);
        stringToUTF8(target, ptr, len);
        return ptr;
    },

    SandboxWsConnect: function (urlPtr) {
        var url = UTF8ToString(urlPtr);
        if (!url) return 0;
        try {
            if (sandboxWsState.socket) {
                try { sandboxWsState.socket.close(); } catch (e) {}
                sandboxWsState.socket = null;
            }
            var ws = new WebSocket(url);
            sandboxWsState.socket = ws;
            ws.onopen = function () {
                if (sandboxWsState.socket === ws) {
                    sandboxWsState.events.push("OPEN");
                }
            };
            ws.onmessage = function (ev) {
                if (sandboxWsState.socket === ws && typeof ev.data === "string") {
                    sandboxWsState.events.push("MSG:" + ev.data);
                }
            };
            ws.onerror = function () {
                if (sandboxWsState.socket === ws) {
                    sandboxWsState.events.push("ERR");
                }
            };
            ws.onclose = function () {
                if (sandboxWsState.socket === ws) {
                    sandboxWsState.socket = null;
                    sandboxWsState.events.push("CLOSE");
                }
            };
            return 1;
        } catch (e) {
            return 0;
        }
    },

    SandboxWsSendText: function (textPtr) {
        if (!sandboxWsState.socket || sandboxWsState.socket.readyState !== 1) return 0;
        var text = UTF8ToString(textPtr);
        sandboxWsState.socket.send(text);
        return 1;
    },

    SandboxWsIsConnected: function () {
        return (sandboxWsState.socket && sandboxWsState.socket.readyState === 1) ? 1 : 0;
    },

    SandboxWsPopEvent: function () {
        if (sandboxWsState.events.length === 0) return 0;
        var item = sandboxWsState.events.shift();
        var len = lengthBytesUTF8(item) + 1;
        var ptr = _malloc(len);
        stringToUTF8(item, ptr, len);
        return ptr;
    },

    SandboxWsIsPointerLocked: function () {
        if (typeof document === "undefined") return 0;
        var el = document.pointerLockElement || document.mozPointerLockElement || document.webkitPointerLockElement;
        return el ? 1 : 0;
    },

    SandboxWsRequestPointerLock: function () {
        if (typeof document === "undefined") return;
        var canvas = document.getElementById("unity-canvas") || document.querySelector("canvas");
        if (canvas && canvas.requestPointerLock) {
            try {
                var p = canvas.requestPointerLock();
                if (p && typeof p.catch === "function") {
                    p.catch(function () {});
                }
            } catch (e) {}
        }
    },

    SandboxWsExitPointerLock: function () {
        if (typeof document === "undefined") return;
        if (document.exitPointerLock) {
            try { document.exitPointerLock(); } catch (e) {}
        }
    },

    SandboxWsClose: function () {
        if (sandboxWsState.socket) {
            try { sandboxWsState.socket.close(); } catch (e) {}
            sandboxWsState.socket = null;
        }
    },

    SandboxWsInstallGuards: function () {
        if (sandboxWsState.guardsBound || typeof window === "undefined") return;
        sandboxWsState.guardsBound = true;

        window.addEventListener("beforeunload", function (e) {
            e.preventDefault();
            e.returnValue = "";
            return "";
        });

        window.addEventListener("keydown", function (e) {
            if (!e) return;
            if (e.key === "F5" || (e.altKey && (e.key === "ArrowLeft" || e.key === "ArrowRight"))) {
                e.preventDefault();
                return;
            }
            if (e.ctrlKey || e.metaKey) {
                var k = (e.key || "").toLowerCase();
                if (k === "c" || k === "v" || k === "x" || k === "a" || k === "z" || k === "y") {
                    return;
                }
                if (k === "w" || k === "t" || k === "n" || k === "r" || k === "s" || k === "p" ||
                    k === "f" || k === "g" || k === "h" || k === "j" || k === "u" || k === "o" || k === "d") {
                    e.preventDefault();
                }
            }
        }, { capture: true });

        if (typeof document !== "undefined" && document.addEventListener) {
            document.addEventListener("fullscreenchange", function () {
                if (typeof navigator === "undefined" || !navigator.keyboard) return;
                try {
                    if (document.fullscreenElement || document.webkitFullscreenElement) {
                        if (navigator.keyboard.lock) {
                            var lockPromise = navigator.keyboard.lock(["KeyW", "KeyT", "KeyN", "KeyR"]);
                            if (lockPromise && typeof lockPromise.catch === "function") {
                                lockPromise.catch(function () {});
                            }
                        }
                    } else if (navigator.keyboard.unlock) {
                        navigator.keyboard.unlock();
                    }
                } catch (e) {}
            });
        }
    },

    SandboxWsToggleFullscreen: function () {
        if (typeof document === "undefined") return;
        try {
            var current = document.fullscreenElement || document.webkitFullscreenElement;
            if (!current) {
                var el = document.documentElement;
                if (el && el.requestFullscreen) {
                    var req = el.requestFullscreen();
                    if (req && typeof req.then === "function") {
                        req.then(function () {
                            if (typeof navigator !== "undefined" && navigator.keyboard && navigator.keyboard.lock) {
                                var lp = navigator.keyboard.lock(["KeyW", "KeyT", "KeyN", "KeyR"]);
                                if (lp && typeof lp.catch === "function") lp.catch(function () {});
                            }
                        }).catch(function () {});
                    }
                } else if (el && el.webkitRequestFullscreen) {
                    el.webkitRequestFullscreen();
                }
            } else {
                if (document.exitFullscreen) {
                    var ex = document.exitFullscreen();
                    if (ex && typeof ex.catch === "function") ex.catch(function () {});
                } else if (document.webkitExitFullscreen) {
                    document.webkitExitFullscreen();
                }
            }
        } catch (e) {}
    },

    SandboxWsGetInitialLanguage: function () {
        var resolved = "ja";
        try {
            var q = "";
            if (typeof window !== "undefined" && window.location) {
                var params = new URLSearchParams(window.location.search || "");
                q = params.get("lang") || "";
            }
            var saved = "";
            if (!q && typeof window !== "undefined" && window.localStorage) {
                try {
                    saved = window.localStorage.getItem("mc_remote_sandbox_lang") || "";
                } catch (e) {}
            }
            var nav = "";
            if (!q && !saved && typeof navigator !== "undefined") {
                nav = navigator.language || "";
            }
            var candidate = (q || saved || nav || "ja").toLowerCase();
            if (candidate.indexOf("en") === 0) {
                resolved = "en";
            }
        } catch (e) {
            resolved = "ja";
        }
        var len = lengthBytesUTF8(resolved) + 1;
        var ptr = _malloc(len);
        stringToUTF8(resolved, ptr, len);
        return ptr;
    },

    SandboxWsSaveLanguage: function (langPtr) {
        if (typeof window === "undefined" || !window.localStorage) return;
        try {
            var lang = UTF8ToString(langPtr) === "en" ? "en" : "ja";
            window.localStorage.setItem("mc_remote_sandbox_lang", lang);
        } catch (e) {}
    }
};

autoAddDeps(SandboxWebSocketPlugin, '$sandboxWsState');
mergeInto(LibraryManager.library, SandboxWebSocketPlugin);
