# MagicaVoxel-Unity-Importer
Add a simple "native" support for .vox files in Unity using ScriptedImporter (and others)

## How to use?

Quite simple actually. 
1. Go to releases
2. Download the zip
3. Extract the zip
4. Drag and drop the **extracted** folder into your Unity Assets

Mow it should work. 

## How does it work?

It runs on pure magic.

## Conflicts?

- Custom Color Pallettes should load normally, but who knows.
- Loading too many things at once might lag.
- Just the general issues

## Is it a "fork" of [korobetski](https://github.com/korobetski/MagicaVoxel-Unity-Importer)s work?

Not really. Sure, most of the script is based on his work. It was abandoned in 2021. An issue opened in 2022 said it did not work with the current Unity version

## What makes it different?

This version is not only adjusted to work for newer versions (Unity 2022.3.62f3 advised), it also now imports animated .vox files as the actual animations they are.
No longer will you have to manually set the animation up, it just is there. Imported on runtime. 

It also is more sturdy since that could be a conflict and works with newer Unity versions.
