using System.Windows.Media;

namespace IPhoneMirror.App.Windows;

public partial class FirstRunSetupWindow
{
    // WPF vector geometry stays crisp at any DPI and does not depend on emoji fonts.
    private static Geometry Symbol(string name)
    {
        var geometry = Geometry.Parse(name switch
        {
            "language" => "M12,2 A10,10 0 1 0 12,22 A10,10 0 1 0 12,2 M2,12 H22 M12,2 C5,8 5,16 12,22 M12,2 C19,8 19,16 12,22",
            "monitor" => "M3,3 H21 V17 H3 Z M12,17 V21 M7,21 H17 M3,13 H21",
            "phone" => "M7,2 H17 Q19,2 19,4 V20 Q19,22 17,22 H7 Q5,22 5,20 V4 Q5,2 7,2 M10,5 H14 M10,19 H14",
            "wireless" => "M2,7 Q12,-1 22,7 M5,11 Q12,5 19,11 M8,15 Q12,11 16,15 M11,19 A1,1 0 1 0 13,19 A1,1 0 1 0 11,19",
            "usb" => "M8,3 H16 V11 Q16,15 12,15 Q8,15 8,11 Z M10,0 V3 M14,0 V3 M12,15 V23 M8,7 H16",
            "game" => "M7,6 H17 Q20,6 21,10 L23,18 Q23,21 20,20 L16,17 H8 L4,20 Q1,21 1,18 L3,10 Q4,6 7,6 M5,11 H11 M8,8 V14 M16,10 H17 M19,13 H20",
            "bluetooth" => "M12,2 V22 L19,16 5,6 M12,2 L19,8 5,18",
            "spark" => "M12,2 L14,9 21,11 14,13 12,20 10,13 3,11 10,9 Z M20,2 V6 M18,4 H22",
            "pin" => "M8,2 H16 L15,8 19,12 V14 H5 V12 L9,8 Z M12,14 V22",
            "sun" => "M12,7 A5,5 0 1 0 12,17 A5,5 0 1 0 12,7 M12,1 V4 M12,20 V23 M1,12 H4 M20,12 H23 M4,4 L6,6 M18,18 L20,20 M4,20 L6,18 M18,6 L20,4",
            "moon" => "M19,17 A9,9 0 1 1 8,3 A8,8 0 0 0 19,17 Z",
            "settings" => "M4,3 V8 M4,12 V21 M12,3 V14 M12,18 V21 M20,3 V5 M20,9 V21 M1,8 H7 V12 H1 Z M9,14 H15 V18 H9 Z M17,5 H23 V9 H17 Z",
            "image" => "M2,3 H22 V21 H2 Z M3,18 L9,12 14,17 18,13 22,17 M15,7 A2,2 0 1 0 19,7 A2,2 0 1 0 15,7",
            "bolt" => "M14,2 L4,14 H11 L10,22 20,10 H13 Z",
            "check" => "M12,2 A10,10 0 1 0 12,22 A10,10 0 1 0 12,2 M6,12 L10,16 18,8",
            "search" => "M10,2 A8,8 0 1 0 10,18 A8,8 0 1 0 10,2 M16,16 L22,22",
            "link" => "M9,15 L15,9 M8,12 L5,15 A3,3 0 0 0 9,19 L12,16 M12,8 L15,5 A3,3 0 0 1 19,9 L16,12",
            _ => "M3,3 H21 V21 H3 Z"
        });
        geometry.Freeze();
        return geometry;
    }
}
