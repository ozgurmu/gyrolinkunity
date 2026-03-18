# Unity Gyro Motion Bridge

Turn your phone into a wireless spatial controller.

This project allows a mobile device to act as a real-time gyroscope input source for a Unity application running on desktop. The phone streams its orientation (quaternion) data over Wi-Fi using UDP, and the Unity desktop app applies this data directly to control a 3D object's rotation.

Instead of using traditional input devices, this system lets you physically move your phone to manipulate objects in 3D space.

## Features

- Real-time orientation streaming (Quaternion-based)
- UDP communication over local Wi-Fi
- Mobile → Desktop motion bridge
- Calibration system (set neutral orientation)
- Optional smoothing (reduce jitter)
- Lightweight and modular architecture


## Use Cases

- Interactive installations
- Game prototyping
- Alternative input systems
- Spatial interaction experiments
- Exhibition setups

## Note

This project has not been tested on a physical device due to the lack of a gyroscope-enabled phone during development. It is expected to work on devices with gyroscope support.

Feedback and contributions are welcome.


This project explores the idea of turning everyday devices into spatial interfaces.
