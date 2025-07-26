<?php
if (!isset($_GET['user']) || !isset($_GET['file']) || !isset($_FILES['file'])) {
    http_response_code(400);
    exit("Missing parameters or uploaded file.");
}

$username = preg_replace('/[^a-zA-Z0-9_\-]/', '', $_GET['user']);
$fileType = $_GET['file'];

$validFiles = ['app_log' => 'app_log.txt', 'clipboard_log' => 'clipboard_log.txt'];
if (!array_key_exists($fileType, $validFiles)) {
    http_response_code(400);
    exit("Invalid file type.");
}

$targetDir = __DIR__ . "/clients/$username";
if (!is_dir($targetDir)) {
    mkdir($targetDir, 0777, true);
}

$targetPath = "$targetDir/" . $validFiles[$fileType];

if (move_uploaded_file($_FILES['file']['tmp_name'], $targetPath)) {
    echo "Upload successful for $username ($fileType)";
} else {
    http_response_code(500);
    echo "Upload failed.";
}

?>
