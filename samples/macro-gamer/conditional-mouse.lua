-- Input is the immutable physical frame that triggered this callback.
-- tap_ms is an optional numeric profile variable; the default is 60 ms.
on_press("Cross", function()
    if input().r2 >= 128 and is_pressed("L1") then
        mouse_tap("LEFT", variable("tap_ms") or 60)
        mouse_move(10, -5)
        mouse_wheel(120)
    end
end)
