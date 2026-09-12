const captionCheckButton = document.querySelector("#caption-check");

captionCheckButton.addEventListener("click", () => {
  write("Выполняется проверка субтитров...");
  playAudio(`/audio/caption_check_ru.wav?cache=${Date.now()}`);
});
