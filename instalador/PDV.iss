; Instalador del Punto de Venta (Inno Setup 6). No se compila a mano: scripts\crear-instalador.ps1
;   - Computadora principal: base de datos (PostgreSQL incluido), servicio del servidor, firewall y la caja 1.
;   - Caja adicional: solo la app de caja, apuntando a la IP de la principal.
; Todo queda en C:\PDV. Actualizar = instalar la versión nueva encima (se conservan datos, caja.json y respaldos).

#ifndef Version
  #define Version "1.0.0"
#endif
#ifndef Origen
  #error Falta /DOrigen=<carpeta preparada por crear-instalador.ps1>
#endif
#define PuertoServidor "5080"

[Setup]
AppId={{6F2B8C1E-4D7A-4E1B-9C35-2A8D0F51B7E4}
AppName=Punto de Venta
AppVersion={#Version}
AppVerName=Punto de Venta {#Version}
AppPublisher=PDV
DefaultDirName=C:\PDV
DisableDirPage=yes
DefaultGroupName=Punto de Venta
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputBaseFilename=Instalar-PDV-{#Version}
SetupIconFile=..\src\PDV.Caja\Recursos\pdv.ico
UninstallDisplayIcon={app}\Caja\PDV.Caja.exe
UninstallDisplayName=Punto de Venta
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
SetupLogging=yes

[Languages]
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"

[Dirs]
; La caja guarda su caja.json junto al programa: cualquier usuario de Windows debe poder escribirlo
Name: "{app}\Caja"; Permissions: users-modify
Name: "{app}\Respaldos"; Check: EsPrincipal

[Files]
Source: "{#Origen}\Caja\*"; DestDir: "{app}\Caja"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#Origen}\Servidor\*"; DestDir: "{app}\Servidor"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: EsPrincipal
Source: "{#Origen}\PostgreSQL\*"; DestDir: "{app}\PostgreSQL"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: EsPrincipal
Source: "{#Origen}\Instalador\*"; DestDir: "{app}\Instalador"; Flags: ignoreversion; Check: EsPrincipal

[Icons]
Name: "{autodesktop}\Punto de Venta"; Filename: "{app}\Caja\PDV.Caja.exe"; WorkingDir: "{app}\Caja"
Name: "{group}\Punto de Venta"; Filename: "{app}\Caja\PDV.Caja.exe"; WorkingDir: "{app}\Caja"
Name: "{group}\Carpeta de respaldos"; Filename: "{app}\Respaldos"; Check: EsPrincipal
Name: "{group}\Quitar el Punto de Venta"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\Caja\PDV.Caja.exe"; WorkingDir: "{app}\Caja"; Description: "Abrir el Punto de Venta"; Flags: postinstall nowait skipifsilent

[UninstallRun]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Instalador\quitar-principal.ps1"" -Raiz ""{app}"""; \
  Flags: runhidden waituntilterminated; RunOnceId: "QuitarServicios"; Check: EsPrincipal

[Code]
var
  TipoPage: TInputOptionWizardPage;
  CajaPage: TInputQueryWizardPage;

function EsPrincipal: Boolean;
begin
  Result := TipoPage.SelectedValueIndex = 0;
end;

function ArchivoCaja: String;
begin
  // WizardDirValue y no {app}: se usa en páginas antes de que {app} exista
  Result := AddBackslash(WizardDirValue) + 'Caja\caja.json';
end;

procedure InitializeWizard;
begin
  TipoPage := CreateInputOptionPage(wpWelcome,
    '¿Qué es esta computadora?',
    'Elija una opción y presione Siguiente.',
    'La computadora principal guarda todas las ventas y el inventario. Debe instalarse primero y quedar siempre ' +
    'encendida mientras se vende. Las demás cajas se conectan a ella por la red.',
    True, False);
  TipoPage.Add('Computadora principal (caja 1): guarda la información y atiende a las demás cajas');
  TipoPage.Add('Caja adicional: se conecta a la computadora principal');
  TipoPage.SelectedValueIndex := StrToIntDef(GetPreviousData('Tipo', '0'), 0);

  CajaPage := CreateInputQueryPage(TipoPage.ID,
    'Conexión con la computadora principal',
    '¿Dónde está la computadora principal?',
    'En la computadora principal, la pantalla de entrada dice abajo "Esta computadora: ...": esa es su IP. ' +
    'El número de esta caja lo da la principal en Menú → Cajas → Agregar caja (se puede cambiar después en ⚙ Configuración).');
  CajaPage.Add('IP de la computadora principal (por ejemplo 192.168.1.10):', False);
  CajaPage.Add('Número de esta caja (2, 3, 4...):', False);
  CajaPage.Values[0] := GetPreviousData('IpPrincipal', '');
  CajaPage.Values[1] := GetPreviousData('NumeroCaja', '2');
end;

procedure RegisterPreviousData(PreviousDataKey: Integer);
begin
  SetPreviousData(PreviousDataKey, 'Tipo', IntToStr(TipoPage.SelectedValueIndex));
  SetPreviousData(PreviousDataKey, 'IpPrincipal', CajaPage.Values[0]);
  SetPreviousData(PreviousDataKey, 'NumeroCaja', CajaPage.Values[1]);
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  // Al actualizar una caja que ya tiene su caja.json no se vuelve a preguntar
  Result := (PageID = CajaPage.ID) and (EsPrincipal or FileExists(ArchivoCaja));
end;

// "192.168.1.10" → "http://192.168.1.10:5080/"; también acepta que escriban la dirección completa
function DireccionServidor(Texto: String): String;
begin
  Result := Trim(Texto);
  if Pos('://', Result) = 0 then
  begin
    if Pos(':', Result) = 0 then
      Result := Result + ':{#PuertoServidor}';
    Result := 'http://' + Result;
  end;
  if Copy(Result, Length(Result), 1) <> '/' then
    Result := Result + '/';
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Numero: Integer;
begin
  Result := True;
  if CurPageID = CajaPage.ID then
  begin
    Numero := StrToIntDef(Trim(CajaPage.Values[1]), 0);
    if Trim(CajaPage.Values[0]) = '' then
    begin
      MsgBox('Escriba la IP de la computadora principal.', mbError, MB_OK);
      Result := False;
    end
    else if Numero < 2 then
    begin
      MsgBox('El número de caja debe ser 2 o mayor (la caja 1 es la computadora principal).', mbError, MB_OK);
      Result := False;
    end;
  end;
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo, MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  if EsPrincipal then
    Result := 'Computadora principal (caja 1)' + NewLine + NewLine +
      Space + 'Se instala en C:\PDV:' + NewLine +
      Space + Space + '• La base de datos (sus datos quedan en C:\PDV\Datos)' + NewLine +
      Space + Space + '• El servidor, que arranca solo con Windows' + NewLine +
      Space + Space + '• El programa de la caja' + NewLine +
      Space + Space + '• Los respaldos se guardan en C:\PDV\Respaldos'
  else if FileExists(ArchivoCaja) then
    Result := 'Caja adicional' + NewLine + Space + 'Se conserva su configuración actual (caja.json).'
  else
    Result := 'Caja adicional' + NewLine +
      Space + 'Computadora principal: ' + DireccionServidor(CajaPage.Values[0]) + NewLine +
      Space + 'Número de esta caja: ' + Trim(CajaPage.Values[1]);
end;

// Al actualizar, los servicios tienen abiertos sus archivos: se detienen antes de copiar
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Codigo: Integer;
begin
  Result := '';
  if EsPrincipal then
  begin
    Exec(ExpandConstant('{sys}\net.exe'), 'stop "PDV Servidor"', '', SW_HIDE, ewWaitUntilTerminated, Codigo);
    Exec(ExpandConstant('{sys}\net.exe'), 'stop "PDV Base de datos"', '', SW_HIDE, ewWaitUntilTerminated, Codigo);
  end;
end;

procedure EscribirCajaJson;
var
  Json: String;
begin
  if FileExists(ArchivoCaja) then
    Exit;
  if EsPrincipal then
    Json := '{' + #13#10 + '  "ServidorUrl": "http://localhost:{#PuertoServidor}/",' + #13#10 + '  "CajaId": 1' + #13#10 + '}'
  else
    Json := '{' + #13#10 + '  "ServidorUrl": "' + DireccionServidor(CajaPage.Values[0]) + '",' + #13#10 +
            '  "CajaId": ' + IntToStr(StrToInt(Trim(CajaPage.Values[1]))) + #13#10 + '}';
  SaveStringToFile(ArchivoCaja, Json, False);
end;

procedure ConfigurarPrincipal;
var
  Codigo: Integer;
begin
  WizardForm.StatusLabel.Caption := 'Preparando la base de datos y el servidor (puede tardar un par de minutos)...';
  WizardForm.ProgressGauge.Style := npbstMarquee;
  try
    if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
         ExpandConstant('-NoProfile -ExecutionPolicy Bypass -File "{app}\Instalador\configurar-principal.ps1" -Raiz "{app}"'),
         '', SW_HIDE, ewWaitUntilTerminated, Codigo) or (Codigo <> 0) then
      MsgBox('No se pudo terminar de preparar la base de datos o el servidor.' + #13#10#13#10 +
        'Los detalles están en ' + ExpandConstant('{app}\instalacion.log') + '.' + #13#10 +
        'Puede volver a correr este instalador: no se pierde nada.', mbError, MB_OK);
  finally
    WizardForm.ProgressGauge.Style := npbstNormal;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    EscribirCajaJson;
    if EsPrincipal then
      ConfigurarPrincipal;
  end;
end;
