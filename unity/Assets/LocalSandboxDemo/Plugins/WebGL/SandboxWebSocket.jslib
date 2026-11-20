var SandboxWebSocketPlugin = {
    $sandboxWsState: {
        socket: null,
        events: [],
        viewerId: null,
        pasteBound: false,
        guardsBound: false,
        resizeBound: false,
        chatInput: null,
        chatComposing: false,
        lastCompositionEndAt: 0,
        chatSubmitQueue: [],
        chatLayout: { nx: 0, ny: 0, nw: 0, nh: 0, visible: false, placeholder: "" },
        lastStyleKey: "",

        getCanvasElement: function () {
            if (typeof document === "undefined") return null;
            return document.getElementById("unity-canvas") || document.querySelector("canvas");
        },

        exitPointerLockIfNeeded: function () {
            if (typeof document === "undefined") return;
            var locked = document.pointerLockElement || document.mozPointerLockElement || document.webkitPointerLockElement;
            if (locked && document.exitPointerLock) {
                try { document.exitPointerLock(); } catch (e) {}
            }
        },

        focusCanvas: function () {
            var canvas = sandboxWsState.getCanvasElement();
            if (!canvas || !canvas.focus) return;
            try {
                canvas.focus({ preventScroll: true });
            } catch (e) {
                try { canvas.focus(); } catch (e2) {}
            }
        },

        focusChatElement: function (setSlashIfEmpty, moveCaretToEnd) {
            var el = sandboxWsState.ensureChatInput();
            if (!el) return;
            sandboxWsState.exitPointerLockIfNeeded();
            if (el.style.display === "none") {
                el.style.display = "block";
            }
            if (setSlashIfEmpty && !el.value) {
                el.value = "/";
            }
            try {
                el.focus({ preventScroll: true });
            } catch (e) {
                try { el.focus(); } catch (e2) {}
            }
            if (moveCaretToEnd && el.setSelectionRange) {
                try {
                    var len = (el.value || "").length;
                    el.setSelectionRange(len, len);
                } catch (e) {}
            }
        },

        applyChatInputPosition: function () {
            var el = sandboxWsState.chatInput;
            if (!el) return;
            var layout = sandboxWsState.chatLayout;
            var canvas = sandboxWsState.getCanvasElement();
            if (!layout.visible || !canvas || layout.nw <= 0 || layout.nh <= 0) {
                if (el.style.display !== "none") {
                    el.style.display = "none";
                }
                sandboxWsState.lastStyleKey = "hidden";
                return;
            }
            var rect = canvas.getBoundingClientRect();
            if (!rect || rect.width <= 0 || rect.height <= 0) {
                if (el.style.display !== "none") {
                    el.style.display = "none";
                }
                sandboxWsState.lastStyleKey = "hidden";
                return;
            }
            var left = rect.left + layout.nx * rect.width;
            var top = rect.top + layout.ny * rect.height;
            var width = Math.max(40, layout.nw * rect.width);
            var height = Math.max(14, layout.nh * rect.height);
            var fontPx = Math.max(9, Math.min(16, Math.round(height * 0.58)));
            var padX = height < 18 ? 4 : 6;
            var placeholder = layout.placeholder || "";
            var key = left.toFixed(1) + "|" + top.toFixed(1) + "|" + width.toFixed(1) + "|" + height.toFixed(1) + "|" + fontPx + "|" + placeholder;
            if (key === sandboxWsState.lastStyleKey && el.style.display === "block") {
                return;
            }
            sandboxWsState.lastStyleKey = key;
            el.style.display = "block";
            el.style.left = left.toFixed(1) + "px";
            el.style.top = top.toFixed(1) + "px";
            el.style.width = width.toFixed(1) + "px";
            el.style.height = height.toFixed(1) + "px";
            el.style.fontSize = fontPx + "px";
            el.style.padding = "0 " + padX + "px";
            if (el.placeholder !== placeholder) {
                el.placeholder = placeholder;
            }
        },

        ensureChatInput: function () {
            if (sandboxWsState.chatInput) return sandboxWsState.chatInput;
            if (typeof document === "undefined" || !document.createElement) return null;

            var el = document.createElement("input");
            el.type = "text";
            el.id = "mc-sandbox-chat-input";
            el.maxLength = 160;
            el.setAttribute("autocomplete", "off");
            el.setAttribute("autocorrect", "off");
            el.setAttribute("autocapitalize", "off");
            el.setAttribute("spellcheck", "false");
            el.style.position = "fixed";
            el.style.zIndex = "2147483647";
            el.style.display = "none";
            el.style.boxSizing = "border-box";
            el.style.margin = "0";
            el.style.padding = "0 6px";
            el.style.background = "rgba(14, 18, 26, 0.92)";
            el.style.color = "#f5f8ff";
            el.style.border = "1px solid rgba(150, 175, 210, 0.48)";
            el.style.borderRadius = "3px";
            el.style.outline = "none";
            el.style.fontFamily = '"Noto Sans Mono CJK JP", "Noto Sans CJK JP", "BIZ UDGothic", "Yu Gothic UI", monospace';
            el.style.lineHeight = "normal";
            el.style.imeMode = "active";

            var host = document.getElementById("unity-fullscreen-container") ||
                document.getElementById("unity-container") ||
                document.body;
            if (!host) return null;
            host.appendChild(el);
            sandboxWsState.chatInput = el;

            el.addEventListener("focus", function () {
                el.style.borderColor = "#7cc4ff";
                el.style.background = "rgba(18, 24, 34, 0.97)";
                sandboxWsState.exitPointerLockIfNeeded();
            });

            el.addEventListener("blur", function () {
                sandboxWsState.chatComposing = false;
                el.style.borderColor = "rgba(150, 175, 210, 0.48)";
                el.style.background = "rgba(14, 18, 26, 0.92)";
            });

            var stopMouseBubble = function (e) {
                if (e && e.stopPropagation) e.stopPropagation();
            };
            el.addEventListener("mousedown", stopMouseBubble);
            el.addEventListener("mouseup", stopMouseBubble);
            el.addEventListener("click", stopMouseBubble);

            el.addEventListener("compositionstart", function () {
                sandboxWsState.chatComposing = true;
            });

            el.addEventListener("compositionend", function () {
                sandboxWsState.chatComposing = false;
                sandboxWsState.lastCompositionEndAt = Date.now();
            });

            el.addEventListener("keydown", function (e) {
                if (!e) return;
                if (e.key === "F5" || (e.altKey && (e.key === "ArrowLeft" || e.key === "ArrowRight"))) {
                    e.preventDefault();
                    e.stopPropagation();
                    return;
                }
                var composing = !!(
                    e.isComposing ||
                    sandboxWsState.chatComposing ||
                    e.keyCode === 229 ||
                    e.key === "Process" ||
                    (Date.now() - sandboxWsState.lastCompositionEndAt < 25)
                );
                if (e.key === "Enter" && !composing) {
                    e.preventDefault();
                    e.stopPropagation();
                    var raw = (el.value || "").trim();
                    el.value = "";
                    if (raw.length > 0) {
                        sandboxWsState.chatSubmitQueue.push(raw);
                    }
                    return;
                }
                if (e.key === "Escape" && !composing) {
                    e.preventDefault();
                    e.stopPropagation();
                    el.blur();
                    sandboxWsState.focusCanvas();
                    return;
                }
                e.stopPropagation();
            });

            var stopKeyBubble = function (e) {
                if (e && e.stopPropagation) e.stopPropagation();
            };
            el.addEventListener("keypress", stopKeyBubble);
            el.addEventListener("keyup", stopKeyBubble);

            if (!sandboxWsState.resizeBound) {
                sandboxWsState.resizeBound = true;
                if (typeof window !== "undefined" && window.addEventListener) {
                    window.addEventListener("resize", function () {
                        sandboxWsState.applyChatInputPosition();
                    });
                }
                if (document.addEventListener) {
                    document.addEventListener("fullscreenchange", function () {
                        sandboxWsState.applyChatInputPosition();
                    });
                }
            }

            return el;
        }
    },

    SandboxWsGetOrCreateViewerId: function () {
        if (!sandboxWsState.pasteBound && typeof window !== "undefined" && window.addEventListener) {
            sandboxWsState.pasteBound = true;
            window.addEventListener("paste", function (ev) {
                if (sandboxWsState.chatInput &&
                    (ev && (ev.target === sandboxWsState.chatInput || document.activeElement === sandboxWsState.chatInput))) {
                    return;
                }
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
        sandboxWsState.ensureChatInput();
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
                    return;
                }
            }
            var el = sandboxWsState.chatInput;
            if (el && el.style.display !== "none" && document.activeElement !== el &&
                !e.ctrlKey && !e.metaKey && !e.altKey && !e.isComposing && e.keyCode !== 229) {
                if (e.key === "/") {
                    e.preventDefault();
                    e.stopPropagation();
                    sandboxWsState.focusChatElement(true, true);
                    return;
                }
                if (e.key === "t" || e.key === "T" || e.key === "Enter") {
                    e.preventDefault();
                    e.stopPropagation();
                    sandboxWsState.focusChatElement(false, true);
                    return;
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
    },

    SandboxWsSyncChatInputLayout: function (nx, ny, nw, nh, placeholderPtr, visible) {
        var el = sandboxWsState.ensureChatInput();
        if (!el) return;
        sandboxWsState.chatLayout.nx = nx;
        sandboxWsState.chatLayout.ny = ny;
        sandboxWsState.chatLayout.nw = nw;
        sandboxWsState.chatLayout.nh = nh;
        sandboxWsState.chatLayout.placeholder = placeholderPtr ? UTF8ToString(placeholderPtr) : "";
        sandboxWsState.chatLayout.visible = !!visible;
        sandboxWsState.applyChatInputPosition();
    },

    SandboxWsIsChatFocused: function () {
        var el = sandboxWsState.chatInput;
        return (el && typeof document !== "undefined" && document.activeElement === el) ? 1 : 0;
    },

    SandboxWsFocusChat: function (setSlashIfEmpty, moveCaretToEnd) {
        sandboxWsState.focusChatElement(!!setSlashIfEmpty, !!moveCaretToEnd);
    },

    SandboxWsBlurChat: function () {
        var el = sandboxWsState.chatInput;
        if (el && typeof document !== "undefined" && document.activeElement === el) {
            try { el.blur(); } catch (e) {}
        }
        sandboxWsState.focusCanvas();
    },

    SandboxWsGetChatText: function () {
        var val = sandboxWsState.chatInput ? (sandboxWsState.chatInput.value || "") : "";
        var len = lengthBytesUTF8(val) + 1;
        var ptr = _malloc(len);
        stringToUTF8(val, ptr, len);
        return ptr;
    },

    SandboxWsSetChatText: function (textPtr, moveCaretToEnd) {
        var el = sandboxWsState.ensureChatInput();
        if (!el) return;
        el.value = textPtr ? (UTF8ToString(textPtr) || "") : "";
        if (moveCaretToEnd && el.setSelectionRange) {
            try {
                var len = el.value.length;
                el.setSelectionRange(len, len);
            } catch (e) {}
        }
    },

    SandboxWsPopSubmittedChat: function () {
        if (sandboxWsState.chatSubmitQueue.length === 0) return 0;
        var item = sandboxWsState.chatSubmitQueue.shift();
        var len = lengthBytesUTF8(item) + 1;
        var ptr = _malloc(len);
        stringToUTF8(item, ptr, len);
        return ptr;
    }
};

autoAddDeps(SandboxWebSocketPlugin, '$sandboxWsState');
mergeInto(LibraryManager.library, SandboxWebSocketPlugin);
