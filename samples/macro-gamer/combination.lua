-- Press L1 and R1 together. The final physical press fires this sequence once.
on_press("L1+R1", function()
    sequence(function()
        key_down("CTRL")
        delay(40)
        key_tap("E", 60)
        key_up("CTRL")
    end)
end)
